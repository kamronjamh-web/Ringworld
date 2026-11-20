using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

// W1 wave precision coverage: the CPU split of the around fraction, the shader's float32 reconstruction of frac(n u), river advection and the shader text contract.
[GlobalClass]
public partial class WaterWaveAnchorSmoke : RefCounted
{
    private const double Scale = 1048576.0;
    private static readonly float[] CycleCounts =
    {
        1, -1, 7, -7, 12345, -12345, 1048575, -1048575, 1048576, -1048576, 1048577, -1048577,
        5650000, -5650000, 9300000, -9300000, 12100000, -12100000, 16777215, -16777215,
    };

    public Godot.Collections.Dictionary Run()
    {
        try
        {
            string splitDetail = CheckSplitExactness();
            string reconstructDetail = CheckReconstruction(out double[] fractions);
            string continuityDetail = CheckContinuity();
            string riverDetail = CheckRiver(fractions);
            CheckShaderContract();
            return new Godot.Collections.Dictionary { { "pass", true }, { "detail", $"{splitDetail}; {reconstructDetail}; {continuityDetail}; {riverDetail}; shader contract ok" } };
        }
        catch (Exception exception)
        {
            return new Godot.Collections.Dictionary { { "pass", false }, { "detail", exception.Message } };
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static double Wrap(double value) => value - Math.Floor(value);
    private static double CyclicDistance(double a, double b)
    {
        double d = Wrap(a - b);
        return Math.Min(d, 1.0 - d);
    }

    private static double[] SweepFractions()
    {
        var list = new System.Collections.Generic.List<double>();
        foreach (double radius in new[] { 5_000_000.0, 6_400_000.0 })
        {
            double circumference = Math.Tau * radius;
            list.Add(0.0);
            list.Add(BitDecrement(circumference) / circumference);
            const int count = 1500;
            for (int i = 0; i < count; i++)
            {
                double around = circumference * i / count + i * 0.37; // not on a regular grid
                list.Add(Wrap(around / circumference));
            }
            for (int i = 0; i < 300; i++) list.Add(Wrap((i * 3.1 + 1.0) / circumference)); // metres from the seam
            for (int i = 0; i < 300; i++) list.Add(Wrap((circumference - i * 2.9 - 0.1) / circumference)); // just below the seam
        }
        return list.ToArray();
    }

    private static double BitDecrement(double value) => Math.BitDecrement(value);

    private static string CheckSplitExactness()
    {
        int tested = 0;
        foreach (double u in SweepFractions())
        {
            RingWaterModel.SplitAroundFraction(u, out double k, out double r);
            Check(k == Math.Floor(k) && k >= 0 && k < Scale, $"k {k} not an integer in [0, 2^20) for u {u:R}");
            Check(r >= 0 && r < 1.0 / Scale, $"r {r:R} outside [0, 2^-20) for u {u:R}");
            double expected = u >= 1.0 ? 0.0 : u;
            Check(k / Scale + r == expected, $"split not exact for u {u:R}: {k / Scale + r:R}");
            tested++;
        }
        (double input, double wrapped)[] wraps = { (-0.25, 0.75), (1.5, 0.5), (2.0, 0.0), (-1.0, 0.0), (3.125, 0.125), (0.0, 0.0), (-1.0 / Scale, 1.0 - 1.0 / Scale) };
        foreach ((double input, double wrapped) in wraps)
        {
            RingWaterModel.SplitAroundFraction(input, out double k, out double r);
            Check(k / Scale + r == wrapped, $"wrap of {input:R} gave {k / Scale + r:R}, expected {wrapped:R}");
        }
        RingWaterModel.SplitAroundFraction(-1e-17, out double tinyK, out double tinyR); // rounds to 1.0: must fold to 0
        Check(tinyK == 0 && tinyR == 0, "u rounding to 1.0 must fold to zero");
        RingWaterModel.SplitAroundFraction(double.NaN, out double nanK, out double nanR);
        Check(nanK == 0 && nanR == 0, "non-finite input must give zero");
        return $"split exact over {tested} fractions";
    }

    private static double ReconstructError(float n, double u)
    {
        RingWaterModel.SplitAroundFraction(u, out double k, out double r);
        double reconstructed = RingWaterModel.ReconstructAnchorPhaseFraction(n, (float)k, (float)r);
        double reference = Wrap(n * (u >= 1.0 ? 0.0 : u));
        return CyclicDistance(reconstructed, reference);
    }

    private static string CheckReconstruction(out double[] fractions)
    {
        var all = new System.Collections.Generic.List<double>(SweepFractions());
        var random = new Random(1234);
        for (int i = 0; i < 2500; i++) all.Add(random.NextDouble());
        fractions = all.ToArray();
        double max = 0.0;
        double worstN = 0.0, worstU = 0.0;
        foreach (float n in CycleCounts)
        {
            foreach (double u in fractions)
            {
                double error = ReconstructError(n, u);
                if (error > max) { max = error; worstN = n; worstU = u; }
            }
        }
        Check(max < 1e-5, $"reconstruction error {max:E3} cycles at n {worstN} u {worstU:R}");
        return $"max reconstruction error {max:E3} cycles over {CycleCounts.Length * fractions.Length} samples";
    }

    private static string CheckContinuity()
    {
        double worst = 0.0;
        foreach (float n in new float[] { 1, 12345, -12345, 5650000, -9300000, 16777215, -16777215 })
        {
            foreach (int j in new[] { 1, 2, 1000, 524288, 1048575, 1048576 })
            {
                double previous = double.NaN;
                for (int t = -50; t <= 50; t++)
                {
                    double u = j / Scale + t * 1e-9;
                    RingWaterModel.SplitAroundFraction(u, out double k, out double r);
                    double phase = RingWaterModel.ReconstructAnchorPhaseFraction(n, (float)k, (float)r);
                    if (!double.IsNaN(previous))
                    {
                        double step = Wrap(phase - previous);
                        if (step > 0.5) step -= 1.0;
                        double tolerance = 1e-5 + Math.Abs(n) * 1e-9;
                        Check(Math.Abs(step) < tolerance, $"phase jump {step:E3} at n {n} j {j} t {t}");
                        worst = Math.Max(worst, Math.Abs(CyclicDistance(step, n * 1e-9)));
                    }
                    previous = phase;
                }
            }
        }
        return $"K-boundary steps smooth (max deviation from the true step {worst:E2} cycles)";
    }

    private static string CheckRiver(double[] fractions)
    {
        double max = 0.0;
        foreach (double clock in new[] { 0.0, 1e3, 1e6, 3e7 })
        {
            double offset = RingWaterModel.RiverAdvectionFraction(clock, 1.35, 5e6);
            Check(offset >= 0.0 && offset < 1.0, $"river offset {offset} outside [0, 1)");
            for (int i = 0; i < fractions.Length; i += 7)
            {
                double u = Wrap(fractions[i] + offset);
                foreach (float n in new float[] { 1, -7, 12345, 5650000, -9300000, 16777215 })
                    max = Math.Max(max, ReconstructError(n, u));
            }
            double next = RingWaterModel.RiverAdvectionFraction(clock + 1.0 / 30.0, 1.35, 5e6);
            double expected = (1.0 / 30.0) * 1.35 * 1.8 / (2.15 * Math.Tau * 5e6);
            Check(CyclicDistance(next - offset, expected) < 1e-12,
                $"river advection step at clock {clock}: {Wrap(next - offset):E6} vs expected {expected:E6}");
        }
        Check(max < 1e-5, $"river reconstruction error {max:E3}");
        return $"river within {max:E3}, advection continuous";
    }

    private static string Read(string path)
    {
        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        Check(file != null, "cannot read " + path);
        return file!.GetAsText().Replace("\r\n", "\n");
    }

    private static void Has(string text, string token, string what) => Check(text.Contains(token, StringComparison.Ordinal), $"{what}: missing `{token}`");

    private static string Function(string text, string signature)
    {
        int start = text.IndexOf(signature, StringComparison.Ordinal);
        Check(start >= 0, $"missing function {signature}");
        int end = text.IndexOf("\n}\n", start, StringComparison.Ordinal);
        Check(end > start, $"unterminated function {signature}");
        return text.Substring(start, end - start);
    }

    private static void CheckShaderContract()
    {
        string common = Read("res://materials/ring_water_common.gdshaderinc");
        foreach (string name in new[] { "world", "tangent", "phase" })
            Has(common, $"global uniform vec4 ring_water_wave_anchor_{name};", "common globals");
        string anchorPhase = Function(common, "float ring_water_anchor_phase(");
        Has(anchorPhase, "& 0xFFFFFu", "ring_water_anchor_phase");
        Has(anchorPhase, "1048576.0", "ring_water_anchor_phase");
        string trainPhase = Function(common, "float ring_water_train_phase(");
        int anchoredBlock = trainPhase.IndexOf("if (anchored) {", StringComparison.Ordinal);
        Check(anchoredBlock >= 0, "ring_water_train_phase: missing `if (anchored)`");
        string block = trainPhase.Substring(anchoredBlock);
        Has(block, "warp_argument += ring_water_anchor_phase(warp_around_cycles, split);", "ring_water_train_phase");
        Has(block, "phase += ring_water_anchor_phase(around_cycles, split);", "ring_water_train_phase");
        Has(common, "ring_water_wave_direction(vec2 direction, float frequency, float kind_clock_scale, out float around_cycles)", "ring_water_wave_direction");
        Has(common, "const vec2 RING_WATER_LAKE_DIRECTION_1 = vec2(-0.916, -0.401);", "lake trains travel downwind (user 2026-11-20)");
        Has(common, "lake ? RING_WATER_LAKE_DIRECTION_1 : vec2(0.916, 0.401)", "lake first train downwind");
        Has(common, "RING_WATER_LAKE_DIRECTION_2", "lake directions");
        Has(common, "RING_WATER_LAKE_DIRECTION_5", "lake directions");

        string proxy = Read("res://materials/ring_ocean_proxy.gdshader");
        Has(proxy, "dFdx(wave_derivative_coordinates)", "proxy");
        Has(proxy, "dFdy(wave_derivative_coordinates)", "proxy");
        foreach (string call in new[] { "ring_water_profile_gradient_filtered(", "ring_water_profile_height_filtered(" })
        {
            int at = proxy.IndexOf(call, StringComparison.Ordinal);
            Check(at >= 0, "proxy: missing " + call);
            string args = proxy.Substring(at, proxy.IndexOf(");", at, StringComparison.Ordinal) - at);
            Has(args, "wave_phase_coordinates", call);
            Has(args, "wave_anchored", call);
        }
        int vertexAt = proxy.IndexOf("ring_water_anchor_coordinates(original_world_position", StringComparison.Ordinal);
        Check(vertexAt >= 0, "proxy: vertex path must call ring_water_anchor_coordinates");
        Has(proxy, "bool vertex_wave_anchored = ring_water_anchor_active();", "proxy vertex");
        Has(proxy, "ring_water_foam_patch_mask(wave_coordinates,", "proxy foam mask");
        Check(!Read("res://materials/ring_ocean_global.gdshader").Contains("ring_water_anchor", StringComparison.Ordinal), "far ocean shader must not use the anchor");

        string project = Read("res://project.godot");
        foreach (string name in new[] { "world", "tangent", "phase" })
        {
            int at = project.IndexOf($"ring_water_wave_anchor_{name}={{", StringComparison.Ordinal);
            Check(at >= 0 && project.Substring(at, 60).Contains("\"type\": \"vec4\"", StringComparison.Ordinal), $"project.godot: ring_water_wave_anchor_{name} must be a vec4 global");
        }
        Check(RingWaterModel.AnchorFractionScale == 1048576.0, "AnchorFractionScale must be 2^20");
    }
}
