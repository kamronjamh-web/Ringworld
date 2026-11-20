using System;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// CPU-authoritative wave trains of the anchored analytic water (docs/reflections-plan.md 10.9, "Smooth turns"). For each water kind and train the CPU
/// decides the quantised around cycles n, the across wavenumber and a continuous phase at the anchor; the shader evaluates
/// phase = 2 pi phase_A + n * delta_theta + k_across * delta_across (+ the warp). A direction change at a fixed anchor leaves the phase at the anchor
/// unchanged, so the pattern pivots on the observer instead of scrambling; anchor motion with a fixed direction advances the phase exactly.
/// Pure and headless-testable; RingWaterModel.WaveAnchor publishes the result.
/// </summary>
public static class RingWaterWaveTrains
{
    public const int KindCount = 3;   // 0 ocean, 1 lake, 2 river
    public const int TrainCount = 5;  // per family (main waves, warps)
    /// <summary>The coordinate scale of each kind (ring_water_profile_*: lakes 1.65, rivers 2.15).</summary>
    public static readonly double[] KindScale = { 1.0, 1.65, 2.15 };

    // Must match materials/ring_water_common.gdshaderinc (ring_water_wave_stack). All kinds travel downwind (user 2026-11-20): a train
    // sin(k.p + rate t) moves along -k for a positive rate, so a direction is the negated wind-relative heading where the rate is positive.
    public static readonly (double X, double Y)[] MainDirections =
        { (-0.916, -0.401), (0.7588, 0.6513), (-0.9999, -0.0112), (0.5787, 0.8156), (-0.9729, 0.2310) };
    public static readonly double[] MainRatios = { 1.0, 1.431, 2.173, 3.117, 5.037 };
    public static readonly double[] MainOffsets = { 0.0, 1.1, 2.4, 3.8, 5.2 };
    public static readonly (double X, double Y)[] WarpDirections =
        { (-0.511, 0.860), (0.766, 0.643), (-0.935, -0.355), (0.252, -0.968), (0.982, -0.188) };
    public static readonly double[] WarpRatios = { 0.173, 0.227, 0.319, 0.271, 0.413 };
    public static readonly double[] WarpOffsets = { 0.0, 1.7, 3.1, 4.6, 0.8 };
    /// <summary>Rotation reference of the wind (ring_water_wind_direction).</summary>
    public static readonly (double X, double Y) WindReference = (0.916, 0.401);

    /// <summary>Rotates a heading from the reference wind (0.916, 0.401) to <paramref name="wind"/>, as ring_water_wind_direction.</summary>
    public static (double X, double Y) Rotate((double X, double Y) direction, (double X, double Y) wind)
    {
        double rl = Math.Sqrt(WindReference.X * WindReference.X + WindReference.Y * WindReference.Y);
        double bx = WindReference.X / rl, by = WindReference.Y / rl;
        double wl = Math.Sqrt(wind.X * wind.X + wind.Y * wind.Y);
        if (!(wl > 1e-12)) { wind = (bx, by); wl = 1; }
        double wx = wind.X / wl, wy = wind.Y / wl;
        double c = bx * wx + by * wy, s = bx * wy - by * wx;
        return (direction.X * c - direction.Y * s, direction.X * s + direction.Y * c);
    }

    /// <summary>
    /// Quantises a train: n = round(dir.x * frequency * C * scale / 2 pi) cycles per turn (C = 2 pi R, the material ring radius), and the across
    /// wavenumber in the shader's scaled coordinate (rad per scaled metre: dir.y * frequency).
    /// </summary>
    public static (double Cycles, double AcrossWavenumber) Quantize((double X, double Y) rotated, double frequency, double radius, double scale)
    {
        double circumference = Math.Tau * Math.Max(1.0, radius) * scale;
        double cycles = Math.Round(rotated.X * frequency * circumference / Math.Tau, MidpointRounding.AwayFromZero);
        return (cycles, rotated.Y * frequency);
    }

    /// <summary>Phase state of one train at the anchor (cycles), continuous across direction changes.</summary>
    public struct TrainState
    {
        public bool Initialized;
        public double Phase;      // spatial anchor phase in cycles
        public double Around;     // the effective around fraction it was last advanced to
        public double Across;     // the across metres it was last advanced to
    }

    /// <summary>
    /// Advances a train's anchor phase to the anchor (around fraction <paramref name="around"/>, including any river advection, and across metres
    /// <paramref name="across"/>) with the current quantisation. The first call sets the absolute phase n u + k y / 2 pi.
    /// <paramref name="acrossPerMetre"/> is the across wavenumber per unscaled metre (scaled wavenumber times the kind scale).
    /// </summary>
    public static void Advance(ref TrainState state, double cycles, double acrossPerMetre, double around, double across)
    {
        if (!state.Initialized)
        {
            state.Phase = Frac(cycles * Frac(around)) + Frac(acrossPerMetre * across / Math.Tau);
            state.Initialized = true;
        }
        else
        {
            double du = around - state.Around;
            du -= Math.Round(du);   // shortest way round the ring: n is an integer, so a whole turn changes nothing
            state.Phase += cycles * du + acrossPerMetre * (across - state.Across) / Math.Tau;
        }
        state.Phase = Frac(state.Phase);
        state.Around = around;
        state.Across = across;
    }

    public static double Frac(double value) => double.IsFinite(value) ? value - Math.Floor(value) : 0.0;
}
