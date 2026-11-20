using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

// Wave-train coverage (docs/reflections-plan.md 10.9): CPU quantisation, pivot on direction change, exact translation, time and river advance,
// the shader/CPU direction contract and the shader/project text contract.
[GlobalClass]
public partial class WaterWaveAnchorSmoke : RefCounted
{
    private static readonly double[] WindDegrees = { 0.0, 17.65, 90.0, 200.0, -135.0 };
    private static readonly double[] MainRates = { 1.0, -1.173, 1.619, -2.071, 2.513 };
    private static readonly double[] WarpRates = { .071, -.053, .041, -.063, .037 };
    private static readonly double[] KindTimeScale = { 1.0, .42, .18 };

    public Godot.Collections.Dictionary Run()
    {
        try
        {
            string quantizeDetail = CheckQuantize();
            string pivotDetail = CheckPivot();
            string translationDetail = CheckTranslation();
            string timeDetail = CheckTimeAndRiver();
            string directionDetail = CheckDirections();
            CheckShaderContract();
            return new Godot.Collections.Dictionary { { "pass", true }, { "detail", $"{quantizeDetail}; {pivotDetail}; {translationDetail}; {timeDetail}; {directionDetail}; shader contract ok" } };
        }
        catch (Exception exception)
        {
            return new Godot.Collections.Dictionary { { "pass", false }, { "detail", exception.Message } };
        }
        finally
        {
            RingWaterModel.ResetTrainStatesForTest();
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

    private static (double X, double Y) WindVector(double degrees) => (Math.Cos(degrees * Math.PI / 180.0), Math.Sin(degrees * Math.PI / 180.0));

    private static (double X, double Y) Heading(bool warp, int train) => warp ? RingWaterWaveTrains.WarpDirections[train] : RingWaterWaveTrains.MainDirections[train];
    private static double Ratio(bool warp, int train) => warp ? RingWaterWaveTrains.WarpRatios[train] : RingWaterWaveTrains.MainRatios[train];

    private static string CheckQuantize()
    {
        const double waveLength = 40.0;
        double baseFrequency = Math.Tau / waveLength;
        double worst = 0.0;
        int tested = 0;
        foreach (double radius in new[] { 5e6, 6.4e6 })
        foreach (double scale in RingWaterWaveTrains.KindScale)
        foreach (double degrees in WindDegrees)
        foreach (bool warp in new[] { false, true })
        for (int train = 0; train < RingWaterWaveTrains.TrainCount; train++)
        {
            double frequency = baseFrequency * Ratio(warp, train);
            var rotated = RingWaterWaveTrains.Rotate(Heading(warp, train), WindVector(degrees));
            var (cycles, across) = RingWaterWaveTrains.Quantize(rotated, frequency, radius, scale);
            double step = Math.Tau / (Math.Tau * radius * scale);   // rad per scaled metre of one cycle per turn
            Check(cycles == Math.Floor(cycles), $"n {cycles} not an integer (R {radius} scale {scale} wind {degrees} warp {warp} train {train})");
            double error = Math.Abs(cycles * step - rotated.X * frequency);
            worst = Math.Max(worst, error / step);
            Check(error <= 0.5 * step * (1.0 + 1e-9), $"quantisation error {error / step:F4} steps (R {radius} scale {scale} wind {degrees} warp {warp} train {train})");
            Check(Math.Abs(across - rotated.Y * frequency) < 1e-12, $"across wavenumber {across} vs {rotated.Y * frequency}");
            tested++;
        }
        return $"quantize ok over {tested} trains (max error {worst:F4} of one step)";
    }

    private static double PhaseAt(in RingWaterWaveTrains.TrainState state, double cycles, double acrossPerMetre, double u, double y)
    {
        double du = u - state.Around;
        du -= Math.Round(du);
        return state.Phase + cycles * du + acrossPerMetre * (y - state.Across) / Math.Tau;
    }

    private static string CheckPivot()
    {
        const double radius = 5e6, waveLength = 40.0, anchorU = 0.31234567891, anchorY = 1234.567;
        double circumference = Math.Tau * radius;
        double baseFrequency = Math.Tau / waveLength;
        double maxAnchor = 0.0, maxPoint = 0.0;
        foreach (double rotation in new[] { 0.01, 1.0, 30.0 })
        foreach (bool warp in new[] { false, true })
        for (int train = 0; train < RingWaterWaveTrains.TrainCount; train++)
        {
            double frequency = baseFrequency * Ratio(warp, train);
            var (cyclesA, kA) = RingWaterWaveTrains.Quantize(RingWaterWaveTrains.Rotate(Heading(warp, train), WindVector(20.0)), frequency, radius, 1.0);
            var (cyclesB, kB) = RingWaterWaveTrains.Quantize(RingWaterWaveTrains.Rotate(Heading(warp, train), WindVector(20.0 + rotation)), frequency, radius, 1.0);
            var state = new RingWaterWaveTrains.TrainState();
            RingWaterWaveTrains.Advance(ref state, cyclesA, kA, anchorU, anchorY);
            var stateA = state;
            RingWaterWaveTrains.Advance(ref state, cyclesB, kB, anchorU, anchorY);
            double anchorDelta = Math.Abs(state.Phase - stateA.Phase);
            maxAnchor = Math.Max(maxAnchor, anchorDelta);
            Check(anchorDelta < 1e-12, $"anchor phase moved {anchorDelta:E3} cycles on a {rotation} degree turn (warp {warp} train {train})");
            foreach ((double du, double dy) in new[] { (500.0 / circumference, 0.0), (0.0, 500.0), (-350.0 / circumference, 420.0) })
            {
                double u = anchorU + du, y = anchorY + dy;
                double before = PhaseAt(stateA, cyclesA, kA, u, y);
                double after = PhaseAt(state, cyclesB, kB, u, y);
                double expected = (cyclesB - cyclesA) * du + (kB - kA) * dy / Math.Tau;
                double error = CyclicDistance(after - before, expected);
                maxPoint = Math.Max(maxPoint, error);
                Check(error < 1e-9, $"point phase change {Wrap(after - before):R} vs expected {Wrap(expected):R} (rotation {rotation} warp {warp} train {train} du {du:E3} dy {dy})");
            }
        }
        return $"pivot ok (anchor delta max {maxAnchor:E2}, point delta error max {maxPoint:E2} cycles)";
    }

    private static string CheckTranslation()
    {
        const double radius = 5e6, waveLength = 40.0;
        double circumference = Math.Tau * radius;
        double baseFrequency = Math.Tau / waveLength;
        double worst = 0.0;
        for (int train = 0; train < RingWaterWaveTrains.TrainCount; train++)
        {
            var (cycles, k) = RingWaterWaveTrains.Quantize(RingWaterWaveTrains.Rotate(RingWaterWaveTrains.MainDirections[train], WindVector(33.0)), baseFrequency * RingWaterWaveTrains.MainRatios[train], radius, 1.0);
            double u = 0.9999999, y = 2500.0;
            var state = new RingWaterWaveTrains.TrainState();
            RingWaterWaveTrains.Advance(ref state, cycles, k, u, y);
            var first = state;
            var random = new Random(4321 + train);
            for (int step = 0; step < 1000; step++)
            {
                double stepAround = (random.NextDouble() * 2.0 - 1.0) * 6.0 / circumference * (step % 50 == 0 ? 40.0 : 1.0);
                u = Wrap(u + stepAround);   // crosses 0 / 1 repeatedly
                y += (random.NextDouble() * 2.0 - 1.0) * 6.0;
                RingWaterWaveTrains.Advance(ref state, cycles, k, u, y);
            }
            foreach ((double du, double dy) in new[] { (0.0, 0.0), (200.0 / circumference, 17.0), (-3000.0 / circumference, -900.0) })
            {
                double pu = Wrap(0.9999999 + du), py = 2500.0 + dy;   // a fixed world point
                double fromFirst = PhaseAt(first, cycles, k, pu, py);
                double fromLast = PhaseAt(state, cycles, k, pu, py);
                double error = CyclicDistance(fromFirst, fromLast);
                worst = Math.Max(worst, error);
                Check(error < 1e-9, $"translation drift {error:E3} cycles for train {train} (first {Wrap(fromFirst):R}, last {Wrap(fromLast):R})");
            }
        }
        return $"translation exact over 1000 steps (max drift {worst:E2} cycles)";
    }

    private static string CheckTimeAndRiver()
    {
        const double radius = 5e6, anchorU = 0.31234567, anchorY = 1234.5, dt = 1.0 / 30.0;
        const float waveSpeed = 1.35f, waveLength = 40f;
        var wind = new Vector2(0.3f, -0.7f);
        double worst = 0.0, worstRiver = 0.0;
        RingWaterModel.ResetTrainStatesForTest();
        foreach (double clock in new[] { 12.5, 4321.75 })
        {
            RingWaterModel.ResetTrainStatesForTest();
            RingWaterModel.ConfigureTrainTablesForTest(clock, wind, waveSpeed, waveLength);
            RingWaterModel.UpdateTrainTables(anchorU, anchorY, radius);
            var before = new Projection[6];
            for (int index = 0; index < 6; index++) before[index] = RingWaterModel.PublishedTrainTable(index / 2, index % 2);
            RingWaterModel.ConfigureTrainTablesForTest(clock + dt, wind, waveSpeed, waveLength);
            RingWaterModel.UpdateTrainTables(anchorU, anchorY, radius);
            double riverStep = RingWaterModel.RiverAdvectionFraction(clock + dt, waveSpeed, radius) - RingWaterModel.RiverAdvectionFraction(clock, waveSpeed, radius);
            for (int kind = 0; kind < 3; kind++)
            for (int family = 0; family < 2; family++)
            for (int train = 0; train < 5; train++)
            {
                Projection a = before[kind * 2 + family], b = RingWaterModel.PublishedTrainTable(kind, family);
                int e = train * 3;
                double nA = a[e / 4][e % 4], nB = b[e / 4][e % 4];
                Check(nA == nB, $"n changed with time: {nA} -> {nB} (kind {kind} family {family} train {train})");
                int p = e + 2;
                double phaseA = a[p / 4][p % 4], phaseB = b[p / 4][p % 4];
                double rate = (family == 1 ? WarpRates : MainRates)[train] * KindTimeScale[kind];
                double timeStep = (RingWorldBounded(clock + dt, waveSpeed, rate) - RingWorldBounded(clock, waveSpeed, rate)) / Math.Tau;
                double expected = timeStep + (kind == 2 ? nB * riverStep : 0.0);
                double error = CyclicDistance(phaseB - phaseA, expected);
                if (kind == 2) worstRiver = Math.Max(worstRiver, error); else worst = Math.Max(worst, error);
                Check(error < 1e-5, $"time advance error {error:E3} cycles at clock {clock} (kind {kind} family {family} train {train}: got {Wrap(phaseB - phaseA):R}, expected {Wrap(expected):R})");
            }
        }
        RingWaterModel.ResetTrainStatesForTest();
        return $"time advance exact (ocean/lake max {worst:E2}, river max {worstRiver:E2} cycles)";
    }

    private static double RingWorldBounded(double clock, double speed, double rate) => RingWaterWindResponse.BoundedPhase(clock, speed, rate);

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

    private static double Num(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static string CheckDirections()
    {
        string common = Read("res://materials/ring_water_common.gdshaderinc");
        double worst = 0.0;
        MatchCollection constants = Regex.Matches(common, @"const vec2 RING_WATER_DIRECTION_(\d) = vec2\(\s*([-0-9.]+)\s*,\s*([-0-9.]+)\s*\);");
        Check(constants.Count == 5, $"expected 5 RING_WATER_DIRECTION_* constants, found {constants.Count}");
        foreach (Match match in constants)
        {
            int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            Check(index >= 0 && index < 5, $"direction index {index + 1}");
            double dx = Math.Abs(Num(match.Groups[2].Value) - RingWaterWaveTrains.MainDirections[index].X);
            double dy = Math.Abs(Num(match.Groups[3].Value) - RingWaterWaveTrains.MainDirections[index].Y);
            worst = Math.Max(worst, Math.Max(dx, dy));
            Check(dx < 1e-4 && dy < 1e-4, $"RING_WATER_DIRECTION_{index + 1} differs from MainDirections by ({dx:E2}, {dy:E2})");
        }
        string stack = Function(common, "void ring_water_wave_stack(");
        Check(!stack.Contains("lake ?", StringComparison.Ordinal), "ring_water_wave_stack must not select directions by `lake ?`");
        MatchCollection calls = Regex.Matches(stack,
            @"RING_WATER_DIRECTION_(\d),\s*([-0-9.]+),\s*(\d+),\s*([-0-9.]+),\s*([-0-9.]+),\s*vec2\(\s*([-0-9.]+)\s*,\s*([-0-9.]+)\s*\),\s*([-0-9.]+),\s*([-0-9.]+),\s*([-0-9.]+),\s*([-0-9.]+),");
        Check(calls.Count == 5, $"expected 5 stack train calls using RING_WATER_DIRECTION_*, found {calls.Count}");
        for (int i = 0; i < 5; i++)
        {
            GroupCollection g = calls[i].Groups;
            Check(int.Parse(g[1].Value, CultureInfo.InvariantCulture) == i + 1, $"stack call {i} uses RING_WATER_DIRECTION_{g[1].Value}");
            Check(Math.Abs(Num(g[2].Value) - RingWaterWaveTrains.MainRatios[i]) < 1e-9, $"stack ratio {i}: {g[2].Value}");
            Check(int.Parse(g[3].Value, CultureInfo.InvariantCulture) == i, $"stack train index {i}: {g[3].Value}");
            Check(Math.Abs(Num(g[4].Value) - MainRates[i]) < 1e-9, $"stack rate coefficient {i}: {g[4].Value}");
            Check(Math.Abs(Num(g[5].Value) - RingWaterWaveTrains.MainOffsets[i]) < 1e-9, $"stack offset {i}: {g[5].Value}");
            double wx = Math.Abs(Num(g[6].Value) - RingWaterWaveTrains.WarpDirections[i].X), wy = Math.Abs(Num(g[7].Value) - RingWaterWaveTrains.WarpDirections[i].Y);
            worst = Math.Max(worst, Math.Max(wx, wy));
            Check(wx < 1e-4 && wy < 1e-4, $"stack warp direction {i} differs by ({wx:E2}, {wy:E2})");
            Check(Math.Abs(Num(g[8].Value) - RingWaterWaveTrains.WarpRatios[i]) < 1e-9, $"stack warp ratio {i}: {g[8].Value}");
            Check(Math.Abs(Num(g[9].Value) - WarpRates[i]) < 1e-9, $"stack warp rate {i}: {g[9].Value}");
            Check(Math.Abs(Num(g[10].Value) - RingWaterWaveTrains.WarpOffsets[i]) < 1e-9, $"stack warp offset {i}: {g[10].Value}");
        }
        var wind = RingWaterWaveTrains.WindReference;
        double windLength = Math.Sqrt(wind.X * wind.X + wind.Y * wind.Y);
        double maxAngle = 0.0;
        for (int i = 0; i < 5; i++)
        {
            double sign = Math.Sign(MainRates[i]);
            var travel = (X: -sign * RingWaterWaveTrains.MainDirections[i].X, Y: -sign * RingWaterWaveTrains.MainDirections[i].Y);
            double dot = travel.X * wind.X + travel.Y * wind.Y;
            Check(dot > 0.0, $"train {i + 1} travels against the wind (dot {dot:F3})");
            double angle = Math.Acos(Math.Clamp(dot / (Math.Sqrt(travel.X * travel.X + travel.Y * travel.Y) * windLength), -1.0, 1.0)) * 180.0 / Math.PI;
            maxAngle = Math.Max(maxAngle, angle);
            Check(angle <= 40.0, $"train {i + 1} travels {angle:F1} degrees off the wind (limit 40)");
        }
        return $"directions match the shader (max diff {worst:E1}), all trains downwind (max {maxAngle:F1} deg)";
    }

    private static void CheckShaderContract()
    {
        string common = Read("res://materials/ring_water_common.gdshaderinc");
        string[] names = { "ocean_main", "ocean_warp", "lake_main", "lake_warp", "river_main", "river_warp" };
        foreach (string name in names)
            Has(common, $"global uniform mat4 ring_water_trains_{name};", "common globals");
        Check(!common.Contains("ring_water_wave_anchor_phase", StringComparison.Ordinal), "common: ring_water_wave_anchor_phase must be gone");
        Has(common, "vec2 ring_water_anchor_coordinates(vec3 sample_world_position)", "common anchor coordinates");
        string trainPhase = Function(common, "float ring_water_train_phase(");
        Has(trainPhase, "int kind", "ring_water_train_phase");
        int anchoredBlock = trainPhase.IndexOf("if (anchored)", StringComparison.Ordinal);
        Check(anchoredBlock >= 0, "ring_water_train_phase: missing `if (anchored)`");
        Has(trainPhase.Substring(anchoredBlock), "ring_water_anchored_train(", "ring_water_train_phase anchored block");

        string proxy = Read("res://materials/ring_ocean_proxy.gdshader");
        Has(proxy, "dFdx(wave_derivative_coordinates)", "proxy");
        Has(proxy, "dFdy(wave_derivative_coordinates)", "proxy");
        const string call = "ring_water_profile_gradient_filtered(";
        int at = proxy.IndexOf(call, StringComparison.Ordinal);
        Check(at >= 0, "proxy: missing " + call);
        string args = proxy.Substring(at, proxy.IndexOf(");", at, StringComparison.Ordinal) - at);
        Has(args, "wave_phase_coordinates", call);
        Has(args, "wave_anchored", call);
        Has(args, "water_gust_response", call);
        Check(!proxy.Contains("gust_whitecap", StringComparison.Ordinal), "proxy: gust_whitecap must be gone");
        int foamAt = proxy.IndexOf("float fft_foam", StringComparison.Ordinal);
        Check(foamAt >= 0, "proxy: missing fft_foam line");
        string foamLine = proxy.Substring(foamAt, proxy.IndexOf('\n', foamAt) - foamAt);
        Check(!foamLine.Contains("water_gust_response", StringComparison.Ordinal), "proxy: fft_foam must not use water_gust_response");
        Has(proxy, "ring_water_foam_patch_mask(wave_coordinates,", "proxy foam mask");
        Check(!Read("res://materials/ring_ocean_global.gdshader").Contains("ring_water_anchor", StringComparison.Ordinal), "far ocean shader must not use the anchor");

        string project = Read("res://project.godot");
        foreach (string name in names)
        {
            int index = project.IndexOf($"ring_water_trains_{name}={{", StringComparison.Ordinal);
            Check(index >= 0 && project.Substring(index, 60).Contains("\"type\": \"mat4\"", StringComparison.Ordinal), $"project.godot: ring_water_trains_{name} must be a mat4 global");
        }
        Check(!project.Contains("ring_water_wave_anchor_phase", StringComparison.Ordinal), "project.godot: ring_water_wave_anchor_phase must be gone");

        // FFT spectrum: the shipped angle is the wind plus 180 degrees (the synthesis travels against the spectrum angle; water_fft_travel_gpu_test).
        Check(Math.Abs(RingWaterFftProvider.SpectrumWindAngle(0f) - Math.PI) < 1e-6, $"SpectrumWindAngle(0) = {RingWaterFftProvider.SpectrumWindAngle(0f)}");
        Check(Math.Abs(RingWaterFftProvider.SpectrumWindAngle(90f) - 1.5 * Math.PI) < 1e-5, $"SpectrumWindAngle(90) = {RingWaterFftProvider.SpectrumWindAngle(90f)}");
    }
}
