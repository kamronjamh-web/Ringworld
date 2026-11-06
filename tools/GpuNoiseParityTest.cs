using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-G0 of docs/gpu-offload-plan.md. Compares the GPU port of FastNoiseLite OpenSimplex2S (materials/terrain_gpu/fnl_opensimplex2s.glslinc)
/// against Godot's FastNoiseLite.GetNoise3D on a local RenderingDevice, and the df64 cylindrical projection
/// (ring_df64.glslinc) against the CPU's (float)(cos/sin(theta) * R). Also probes whether GLSL double compiles and runs through RD
/// and records GPU time per million noise evaluations. Tolerances are the plan's 4.5 rows:
///   noise      bit-identical on >= 99.99 % of inputs per configuration, max abs diff <= 2e-7
///   projection identical on >= 99.999 %, else 1 ULP
/// Run (windowed Vulkan, not --headless; no other Godot process running):
///   Godot --path . --script res://tools/gpu_noise_parity_test.gd
/// Prints GPU_NOISE_PARITY=PASS|FAIL and writes .godot/gpu_noise_parity/report.json.
/// </summary>
public partial class GpuNoiseParityTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const double Width = 318_000d;
    private const double Tau = Math.PI * 2d;
    private const int HeadlineN = 1_000_000;
    private const int ExtraN = 200_000;
    private const double MaxAbsTolerance = 2e-7;
    private const double NoiseExactPercentMin = 99.99;
    private const double ProjectionExactPercentMin = 99.999;

    private const string ShaderDir = "res://materials/terrain_gpu/";

    private RenderingDevice _rd = null!;
    private readonly List<string> _lines = new();
    private readonly List<string> _failures = new();
    private readonly StringBuilder _json = new();
    private bool _timestampsWork;

    private record struct NoiseCase(string Name, int Fractal, int Octaves, double Scale, float Gain, float Lacunarity, int Seed, int N);
    private struct Stats { public long Count, Exact; public long MaxUlp; public double MaxAbs; public int WorstIndex; }

    public int Run(string outputDirectory)
    {
        try
        {
            _rd = RenderingServer.CreateLocalRenderingDevice();
            if (_rd == null) { Console.Error.WriteLine("GPU_NOISE_PARITY=FAIL no local RenderingDevice"); return 1; }
            Log($"device: {_rd.GetDeviceName()} ({_rd.GetDeviceVendorName()})");
            _json.Append("{\n");
            _json.Append($"  \"device\": \"{_rd.GetDeviceName()}\",\n");

            RunNoise();
            RunProjection();
            // fp64 last: if the driver rejects it, everything above is already recorded.
            RunDoubleProbe();

            _json.Append("  \"done\": true\n}\n");
            string dir = string.IsNullOrEmpty(outputDirectory) ? ProjectSettings.GlobalizePath("res://.godot/gpu_noise_parity") : outputDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), _json.ToString());
            File.WriteAllText(Path.Combine(dir, "report.txt"), string.Join("\n", _lines) + "\n");
        }
        catch (Exception e) { Console.Error.WriteLine("GPU_NOISE_PARITY=FAIL exception " + e); return 1; }
        finally { _rd?.Free(); }

        if (_failures.Count != 0) { Console.Error.WriteLine("GPU_NOISE_PARITY=FAIL " + string.Join(" | ", _failures.Take(20))); return 1; }
        Console.WriteLine("GPU_NOISE_PARITY=PASS");
        return 0;
    }

    private void Log(string line) { _lines.Add(line); Console.WriteLine("GPU_NOISE_DETAIL " + line); }

    // ---------------------------------------------------------------- shader helpers

    private static string LoadShader(string file)
    {
        string text = Godot.FileAccess.GetFileAsString(ShaderDir + file).Replace("\r\n", "\n");
        text = text.Replace("#[compute]\n", "");
        int at;
        while ((at = text.IndexOf("#include \"", StringComparison.Ordinal)) >= 0)
        {
            int start = at + "#include \"".Length;
            int end = text.IndexOf('"', start);
            string inc = text.Substring(start, end - start);
            int lineEnd = text.IndexOf('\n', end);
            text = text.Substring(0, at) + Godot.FileAccess.GetFileAsString(ShaderDir + inc).Replace("\r\n", "\n") + text.Substring(lineEnd);
        }
        return text;
    }

    private bool Compile(string name, string source, out Rid shader, out Rid pipeline, out string error)
    {
        shader = default; pipeline = default; error = "";
        var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = source };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(src);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { error = "compile: " + spirv.CompileErrorCompute; return false; }
        shader = _rd.ShaderCreateFromSpirV(spirv, name);
        if (!shader.IsValid) { error = "shader_create_from_spirv returned an invalid RID"; return false; }
        pipeline = _rd.ComputePipelineCreate(shader);
        if (!pipeline.IsValid) { error = "compute_pipeline_create returned an invalid RID"; return false; }
        return true;
    }

    private static byte[] Bytes(float[] data) { var b = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length); return b; }
    private static float[] Floats(byte[] data) { var f = new float[data.Length / 4]; Buffer.BlockCopy(data, 0, f, 0, data.Length); return f; }

    /// <summary>Dispatches pipeline over count invocations; returns wall ms and (when available) GPU ms of the dispatch.</summary>
    private (double wallMs, double gpuMs) Dispatch(Rid pipeline, Rid uniformSet, byte[] push, int count)
    {
        var sw = Stopwatch.StartNew();
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, pipeline);
        _rd.ComputeListBindUniformSet(list, uniformSet, 0);
        _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        _rd.CaptureTimestamp("gnp_begin");
        _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
        _rd.ComputeListEnd();
        _rd.CaptureTimestamp("gnp_end");
        _rd.Submit();
        _rd.Sync();
        double wall = sw.Elapsed.TotalMilliseconds;
        double gpu = -1;
        uint n = _rd.GetCapturedTimestampsCount();
        ulong begin = 0, end = 0; bool hb = false, he = false;
        for (uint i = 0; i < n; i++)
        {
            string tn = _rd.GetCapturedTimestampName(i);
            if (tn == "gnp_begin") { begin = _rd.GetCapturedTimestampGpuTime(i); hb = true; }
            else if (tn == "gnp_end") { end = _rd.GetCapturedTimestampGpuTime(i); he = true; }
        }
        if (hb && he && end > begin) { gpu = (end - begin) / 1e6; _timestampsWork = true; }
        return (wall, gpu);
    }

    private static Godot.Collections.Array<RDUniform> StorageUniforms(Rid a, Rid b)
    {
        var ua = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; ua.AddId(a);
        var ub = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; ub.AddId(b);
        return new Godot.Collections.Array<RDUniform> { ua, ub };
    }

    // ---------------------------------------------------------------- noise

    private static void FillNoiseInputs(float[] xyz, int n, double scale, int rngSeed)
    {
        var rng = new Random(rngSeed);
        double R = Circumference / Tau;
        for (int i = 0; i < n; i++)
        {
            double x, y, z;
            int cat = i % 10;
            double theta = rng.NextDouble() * Tau;
            switch (cat)
            {
                case 5: // seam wrap: within 1e-3 rad of theta 0, either side, and across edges
                    theta = (rng.NextDouble() * 2 - 1) * 1e-3;
                    goto default;
                case 6: // small magnitude
                    x = (rng.NextDouble() * 2 - 1) * 1000d; y = (rng.NextDouble() * 2 - 1) * 1000d; z = (rng.NextDouble() * 2 - 1) * 1000d;
                    if ((i / 10) % 7 == 0) { x *= 1e-4; y *= 1e-4; z *= 1e-4; }
                    break;
                case 7: // large magnitude
                    x = (rng.NextDouble() * 2 - 1) * 1e7; y = (rng.NextDouble() * 2 - 1) * 1e7; z = (rng.NextDouble() * 2 - 1) * 1e7;
                    break;
                case 8: // exact lattice multiples of the scale (frequency product lands on integers: negative-integer FastFloor path)
                    x = rng.Next(-4096, 4096) * scale; y = rng.Next(-4096, 4096) * scale; z = rng.Next(-64, 64) * scale;
                    break;
                case 9: // ring xy, across beyond the edges
                    x = Math.Cos(theta) * R; y = Math.Sin(theta) * R; z = (rng.NextDouble() * 2 - 1) * 5000d - (rng.Next(2) == 0 ? 0 : 0);
                    break;
                default:
                    x = Math.Cos(theta) * R; y = Math.Sin(theta) * R;
                    z = rng.NextDouble() * Width;
                    if (cat == 5) { int e = rng.Next(4); if (e == 0) z = 0; else if (e == 1) z = Width; }
                    break;
            }
            xyz[i * 3] = (float)x; xyz[i * 3 + 1] = (float)y; xyz[i * 3 + 2] = (float)z;
        }
    }

    private static long OrderedBits(float f)
    {
        int b = BitConverter.SingleToInt32Bits(f);
        return b < 0 ? (long)int.MinValue - b : b;
    }

    private static float[] CpuNoise(NoiseCase c, float[] xyz, int n)
    {
        var result = new float[n];
        const int chunk = 8192;
        int chunks = (n + chunk - 1) / chunk;
        Parallel.For(0, chunks, ci =>
        {
            using var noise = new FastNoiseLite
            {
                Seed = c.Seed,
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
                Frequency = (float)(1d / c.Scale),
                FractalType = c.Fractal == 0 ? FastNoiseLite.FractalTypeEnum.None : c.Fractal == 1 ? FastNoiseLite.FractalTypeEnum.Fbm : FastNoiseLite.FractalTypeEnum.Ridged,
                FractalOctaves = c.Octaves,
                FractalGain = c.Gain,
                FractalLacunarity = c.Lacunarity,
            };
            int end = Math.Min(n, (ci + 1) * chunk);
            for (int i = ci * chunk; i < end; i++) result[i] = noise.GetNoise3D(xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
        });
        return result;
    }

    private static byte[] NoisePush(NoiseCase c, int n, int repeat)
    {
        var b = new byte[32];
        Buffer.BlockCopy(new[] { c.Seed, c.Fractal, c.Octaves, n }, 0, b, 0, 16);
        Buffer.BlockCopy(new[] { (float)(1d / c.Scale), c.Lacunarity, c.Gain }, 0, b, 16, 12);
        Buffer.BlockCopy(new[] { repeat }, 0, b, 28, 4);
        return b;
    }

    private void RunNoise()
    {
        string source = LoadShader("fnl_noise_probe.glsl");
        if (!Compile("fnl_noise_probe", source, out Rid shader, out Rid pipeline, out string error))
        {
            _failures.Add("noise shader: " + error);
            Log("noise shader FAILED: " + error);
            return;
        }

        var cases = new List<NoiseCase>();
        int s = 0;
        cases.Add(new("none", 0, 1, 256, 0.5f, 2.0f, 101, HeadlineN));
        for (int o = 1; o <= 5; o++) cases.Add(new($"fbm{o}", 1, o, 256, 0.5f, 2.0f, 211 + 100 * o + (s++), HeadlineN));
        for (int o = 1; o <= 5; o++) cases.Add(new($"ridged{o}", 2, o, 256, 0.5f, 2.0f, o == 3 ? -987654321 : 503 + 100 * o, HeadlineN));
        int rs = 0;
        foreach (double scale in new[] { 32d, 4000d, 40000d })
        {
            cases.Add(new($"none_s{scale}", 0, 1, scale, 0.5f, 2.0f, 1009 + rs++, ExtraN));
            cases.Add(new($"fbm4_s{scale}_g047_l185", 1, 4, scale, 0.47f, 1.85f, int.MaxValue - 5 - rs++, ExtraN));
            cases.Add(new($"ridged3_s{scale}_g047_l185", 2, 3, scale, 0.47f, 1.85f, -2000000011 + rs++, ExtraN));
        }

        _json.Append("  \"noise\": [\n");
        long totalCount = 0, totalExact = 0; long overallMaxUlp = 0; double overallMaxAbs = 0;
        double totalWallMs = 0; double headlineGpuMsPerM = 0;
        var rows = new List<string>();
        foreach (NoiseCase c in cases)
        {
            var xyz = new float[c.N * 3];
            FillNoiseInputs(xyz, c.N, c.Scale, 12345 + c.Seed);
            float[] cpu = CpuNoise(c, xyz, c.N);

            Rid inBuf = _rd.StorageBufferCreate((uint)(xyz.Length * 4), Bytes(xyz));
            Rid outBuf = _rd.StorageBufferCreate((uint)(c.N * 4));
            Rid set = _rd.UniformSetCreate(StorageUniforms(inBuf, outBuf), shader, 0);
            Dispatch(pipeline, set, NoisePush(c, c.N, 1), c.N); // warm up / result
            float[] gpu = Floats(_rd.BufferGetData(outBuf));

            // timing: best of 3 on this input set
            double bestWall = double.MaxValue, bestGpu = double.MaxValue;
            for (int t = 0; t < 3; t++)
            {
                var (w, g) = Dispatch(pipeline, set, NoisePush(c, c.N, 1), c.N);
                bestWall = Math.Min(bestWall, w); if (g >= 0) bestGpu = Math.Min(bestGpu, g);
            }
            double perM = 1e6 / c.N;
            double wallPerM = bestWall * perM, gpuPerM = bestGpu == double.MaxValue ? -1 : bestGpu * perM;

            var st = new Stats { Count = c.N };
            for (int i = 0; i < c.N; i++)
            {
                float a = cpu[i], b = gpu[i];
                long ulp = Math.Abs(OrderedBits(a) - OrderedBits(b));
                double abs = Math.Abs((double)a - b);
                if (float.IsNaN(b) || float.IsNaN(a)) { ulp = long.MaxValue / 4; abs = double.PositiveInfinity; }
                if (ulp == 0) st.Exact++;
                if (ulp > st.MaxUlp) { st.MaxUlp = ulp; st.WorstIndex = i; }
                if (abs > st.MaxAbs) st.MaxAbs = abs;
            }
            double pct = 100.0 * st.Exact / st.Count;
            string worst = st.MaxUlp == 0 ? "" : $" worst@in=({xyz[st.WorstIndex * 3]:R},{xyz[st.WorstIndex * 3 + 1]:R},{xyz[st.WorstIndex * 3 + 2]:R}) cpu={cpu[st.WorstIndex]:R} gpu={gpu[st.WorstIndex]:R}";
            Log($"noise {c.Name,-26} n={c.N} exact={pct:F5}% maxUlp={st.MaxUlp} maxAbs={st.MaxAbs:E3} wall={wallPerM:F2}ms/M gpu={(gpuPerM < 0 ? "n/a" : gpuPerM.ToString("F2"))}ms/M{worst}");
            rows.Add($"    {{\"name\":\"{c.Name}\",\"n\":{c.N},\"exact_percent\":{pct:F6},\"max_ulp\":{st.MaxUlp},\"max_abs\":{st.MaxAbs:E4},\"wall_ms_per_million\":{wallPerM:F3},\"gpu_ms_per_million\":{gpuPerM:F3}}}");
            if (pct < NoiseExactPercentMin) _failures.Add($"noise {c.Name}: exact {pct:F4}% < {NoiseExactPercentMin}%");
            if (st.MaxAbs > MaxAbsTolerance) _failures.Add($"noise {c.Name}: max abs {st.MaxAbs:E3} > {MaxAbsTolerance:E1}");
            totalCount += st.Count; totalExact += st.Exact; overallMaxUlp = Math.Max(overallMaxUlp, st.MaxUlp); overallMaxAbs = Math.Max(overallMaxAbs, st.MaxAbs);
            totalWallMs += bestWall;
            if (c.Name == "fbm5") headlineGpuMsPerM = gpuPerM;

            _rd.FreeRid(set); _rd.FreeRid(inBuf); _rd.FreeRid(outBuf);
        }
        _json.Append(string.Join(",\n", rows)).Append("\n  ],\n");
        Log($"noise OVERALL evaluations={totalCount} exact={100.0 * totalExact / totalCount:F5}% maxUlp={overallMaxUlp} maxAbs={overallMaxAbs:E3} timestamps={(_timestampsWork ? "yes" : "no")}");
        _json.Append($"  \"noise_overall\": {{\"count\":{totalCount},\"exact_percent\":{100.0 * totalExact / totalCount:F6},\"max_ulp\":{overallMaxUlp},\"max_abs\":{overallMaxAbs:E4}}},\n");

        _rd.FreeRid(pipeline); _rd.FreeRid(shader);
    }

    // ---------------------------------------------------------------- df64 projection

    private static vec2d Split(double d) { float hi = (float)d; return new vec2d(hi, (float)(d - hi)); }
    private readonly record struct vec2d(float Hi, float Lo);

    private void RunProjection()
    {
        string source = LoadShader("ring_df64_projection_probe.glsl");
        if (!Compile("ring_df64_projection_probe", source, out Rid shader, out Rid pipeline, out string error))
        {
            _failures.Add("projection shader: " + error);
            Log("projection shader FAILED: " + error);
            return;
        }
        const int n = 1_000_000;
        double C = Circumference, R = C / Tau, k = Tau / C;
        var input = new float[n * 8];
        var cpuX = new float[n]; var cpuY = new float[n];
        var rng = new Random(777);
        for (int i = 0; i < n; i++)
        {
            long a0 = rng.NextDouble() < 0.1 ? (rng.Next(2) == 0 ? rng.Next(0, 20000) : Circumference - 1 - rng.Next(0, 20000)) : (long)(rng.NextDouble() * C);
            int dA = rng.Next(-10000, 10001);
            float warp = (float)((rng.NextDouble() * 2 - 1) * 400d);
            double theta0 = a0 / C * Tau;
            vec2d c0 = Split(Math.Cos(theta0)), s0 = Split(Math.Sin(theta0));
            input[i * 8] = c0.Hi; input[i * 8 + 1] = c0.Lo; input[i * 8 + 2] = s0.Hi; input[i * 8 + 3] = s0.Lo;
            input[i * 8 + 4] = dA; input[i * 8 + 5] = warp;
            // CPU reference exactly as TerrainGenerationHeightModel.CylindricalProjection
            double around = (double)a0 + dA + warp;
            double wrapped = around % C; if (wrapped < 0) wrapped += C;
            double theta = wrapped / C * Tau;
            cpuX[i] = (float)(Math.Cos(theta) * R); cpuY[i] = (float)(Math.Sin(theta) * R);
        }
        var push = new byte[32];
        vec2d kd = Split(k), rd2 = Split(R);
        Buffer.BlockCopy(new[] { kd.Hi, kd.Lo, rd2.Hi, rd2.Lo }, 0, push, 0, 16);
        Buffer.BlockCopy(new[] { n, 0, 0, 0 }, 0, push, 16, 16);

        Rid inBuf = _rd.StorageBufferCreate((uint)(input.Length * 4), Bytes(input));
        Rid outBuf = _rd.StorageBufferCreate((uint)(n * 8));
        Rid set = _rd.UniformSetCreate(StorageUniforms(inBuf, outBuf), shader, 0);
        Dispatch(pipeline, set, push, n);
        float[] gpu = Floats(_rd.BufferGetData(outBuf));

        long exact = 0, maxUlp = 0; double maxAbs = 0; int worstI = 0; long total = 0; long ulp1 = 0;
        for (int i = 0; i < n; i++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                float a = axis == 0 ? cpuX[i] : cpuY[i], b = gpu[i * 2 + axis];
                long ulp = Math.Abs(OrderedBits(a) - OrderedBits(b));
                double abs = Math.Abs((double)a - b);
                if (float.IsNaN(b)) { ulp = long.MaxValue / 4; abs = double.PositiveInfinity; }
                total++;
                if (ulp == 0) exact++; else if (ulp == 1) ulp1++;
                if (ulp > maxUlp) { maxUlp = ulp; worstI = i; }
                if (abs > maxAbs) maxAbs = abs;
            }
        }
        double pct = 100.0 * exact / total;
        Log($"projection df64 n={total} identical={pct:F6}% oneUlp={ulp1} maxUlp={maxUlp} maxAbs={maxAbs:E3}m (worst point {worstI}: cpu=({cpuX[worstI]:R},{cpuY[worstI]:R}) gpu=({gpu[worstI * 2]:R},{gpu[worstI * 2 + 1]:R}))");
        _json.Append($"  \"projection_df64\": {{\"count\":{total},\"identical_percent\":{pct:F6},\"one_ulp\":{ulp1},\"max_ulp\":{maxUlp},\"max_abs_m\":{maxAbs:E4}}},\n");
        if (pct < ProjectionExactPercentMin) _failures.Add($"projection: identical {pct:F5}% < {ProjectionExactPercentMin}%");
        if (maxUlp > 1) _failures.Add($"projection: max ULP {maxUlp} > 1");

        _rd.FreeRid(set); _rd.FreeRid(inBuf); _rd.FreeRid(outBuf); _rd.FreeRid(pipeline); _rd.FreeRid(shader);
    }

    // ---------------------------------------------------------------- fp64 probe

    private const string DoubleProbe = @"#version 450
#extension GL_ARB_gpu_shader_fp64 : enable
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer Inputs { float v[]; } inputs;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Outputs { float v[]; } outputs;
layout(push_constant, std430) uniform Params { int count; int iterations; int useDouble; int pad; } pc;
void main()
{
	uint idx = gl_GlobalInvocationID.x;
	if (idx >= uint(pc.count)) return;
	float seedValue = inputs.v[idx];
	// 1 + 1e-10 only survives double precision: (sum - 1) * 1e10 == 1 in double, 0 in float
	double d = double(1.0) + double(1e-10) * double(seedValue);
	float exactnessProbe = float((d - double(1.0)) * double(1e10));
	float result = 0.0;
	if (pc.useDouble != 0)
	{
		double a = double(seedValue) + 0.5lf;
		for (int i = 0; i < pc.iterations; i++) a = a * 1.0000001lf + 1e-9lf;
		result = exactnessProbe + float(a) * 1e-30;
	}
	else
	{
		float fa = seedValue + 0.5;
		for (int i = 0; i < pc.iterations; i++) fa = fa * 1.0000001 + 1e-9;
		result = fa * 1e-30;
	}
	outputs.v[idx] = result;
}
";

    private void RunDoubleProbe()
    {
        string status;
        bool compiled = false, created = false, ran = false; string detail = "";
        var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = DoubleProbe };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(src);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            status = "UNAVAILABLE (GLSL compile error)"; detail = spirv.CompileErrorCompute.Replace("\n", " ").Trim();
        }
        else
        {
            compiled = true;
            Rid shader = _rd.ShaderCreateFromSpirV(spirv, "fp64_probe");
            Rid pipeline = shader.IsValid ? _rd.ComputePipelineCreate(shader) : default;
            if (!shader.IsValid) { status = "UNAVAILABLE (SPIR-V compiled, shader_create_from_spirv failed)"; }
            else if (!pipeline.IsValid) { status = "UNAVAILABLE (SPIR-V compiled, compute_pipeline_create failed)"; }
            else
            {
                created = true;
                const int count = 1 << 20;
                var input = new float[count];
                for (int i = 0; i < count; i++) input[i] = 1f;
                Rid inBuf = _rd.StorageBufferCreate((uint)(count * 4), Bytes(input));
                Rid outBuf = _rd.StorageBufferCreate((uint)(count * 4));
                Rid set = _rd.UniformSetCreate(StorageUniforms(inBuf, outBuf), shader, 0);
                byte[] Push(int iters, int useDouble) { var b = new byte[16]; Buffer.BlockCopy(new[] { count, iters, useDouble, 0 }, 0, b, 0, 16); return b; }
                Dispatch(pipeline, set, Push(1, 1), count);
                float result = Floats(_rd.BufferGetData(outBuf))[0];
                ran = true;
                bool trueDouble = Math.Abs(result - 1.0f) < 0.01f;
                status = trueDouble ? "AVAILABLE (compiles, runs, 64-bit result verified)" : $"RUNS BUT NOT FP64 PRECISION (probe returned {result})";
                if (trueDouble)
                {
                    // throughput: dependent mul-add chain, double vs float
                    const int iters = 2000;
                    double bestD = double.MaxValue, bestF = double.MaxValue;
                    for (int t = 0; t < 3; t++)
                    {
                        var (wd, gd) = Dispatch(pipeline, set, Push(iters, 1), count);
                        var (wf, gf) = Dispatch(pipeline, set, Push(iters, 0), count);
                        bestD = Math.Min(bestD, gd >= 0 ? gd : wd); bestF = Math.Min(bestF, gf >= 0 ? gf : wf);
                    }
                    detail = $"dependent mul-add chain {count} invocations x {iters}: double {bestD:F2} ms, float {bestF:F2} ms, double/float time ratio {bestD / bestF:F1}x (device-reported fp64 ratio on a 4070 is 64x)";
                }
                _rd.FreeRid(set); _rd.FreeRid(inBuf); _rd.FreeRid(outBuf);
            }
            if (pipeline.IsValid) _rd.FreeRid(pipeline);
            if (shader.IsValid) _rd.FreeRid(shader);
        }
        Log($"fp64: {status} compiled={compiled} pipeline={created} ran={ran} {detail}");
        _json.Append($"  \"fp64\": {{\"status\":\"{status}\",\"glsl_compiled\":{compiled.ToString().ToLowerInvariant()},\"pipeline_created\":{created.ToString().ToLowerInvariant()},\"ran\":{ran.ToString().ToLowerInvariant()},\"detail\":\"{detail.Replace("\"", "'")}\"}},\n");
    }
}
