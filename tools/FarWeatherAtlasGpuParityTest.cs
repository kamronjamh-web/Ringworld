using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// GPU-4 parity: the GPU-built far weather atlas, layer atlas and fine layer atlas against the CPU build for the same inputs. Reports per-channel max / mean error
/// (RGBA8 units), writes cpu / gpu PNGs for tools/media/ffmpeg_tools.ps1 compare, and fails above the tolerances below.
/// Driven by tools/far_weather_atlas_gpu_parity_gpu_test.gd. The tolerances allow float hash / ellipse-edge differences at a few texels, not systematic error.
/// </summary>
public partial class FarWeatherAtlasGpuParityTest : RefCounted
{
    private RingScaledFarViewport? _far;
    private double _time;
    private int _job, _buffer, _fineJob, _fineBuffer;
    private double _fineOrigin; private float _fineTexel;
    private readonly byte[][] _gpu = new byte[3][];
    private readonly int[][] _done = { new int[3] };
    public string Report = "";
    public string OutputDirectory = "";

    public bool Start(RingScaledFarViewport far, double time, double fineOrigin, float fineTexel)
    {
        _far = far; _time = time; _fineOrigin = fineOrigin; _fineTexel = fineTexel;
        _job = far.QueueFarWeatherAtlasGpuForTest(time, out _buffer);
        _fineJob = far.QueueFarFineAtlasGpuForTest(time, fineOrigin, fineTexel, out _fineBuffer);
        return _job != 0 && _fineJob != 0;
    }

    private int _stage;
    /// <summary>Polls the queued jobs and readbacks; true when the comparison can run.</summary>
    public bool Poll()
    {
        RingFarWeatherAtlasGpu gpu = _far!.FarAtlasGpuForTest!;
        if (_stage == 0 && gpu.WeatherJobDone(_job) && gpu.FineJobDone(_fineJob))
        {
            gpu.QueueReadback(0, _buffer, _gpu, 0, _done[0]);
            gpu.QueueReadback(1, _buffer, _gpu, 1, _done[0]);
            gpu.QueueReadback(2, _fineBuffer, _gpu, 2, _done[0]);
            _stage = 1;
        }
        return _stage == 1 && _done[0][0] == 1 && _done[0][1] == 1 && _done[0][2] == 1;
    }

    public int Compare(string label)
    {
        var failures = new List<string>();
        _far!.BuildFarWeatherAtlasCpuReference(_time, out byte[] cpuWeather, out byte[] cpuLayers, out int w, out int h);
        byte[] cpuFine = _far.BuildFarFineAtlasCpuReference(_time, _fineOrigin, _fineTexel);
        // channel tolerances (RGBA8 units): max, mean
        Check("weather", cpuWeather, _gpu[0], w, h, new[] { 2, 2, 1, 1 }, new[] { 0.1, 0.1, 0.1, 0.1 }, label, failures);
        Check("layers", cpuLayers, _gpu[1], w, h, new[] { 2, 2, 2, 2 }, new[] { 0.1, 0.1, 0.1, 0.1 }, label, failures);
        Check("fine", cpuFine, _gpu[2], 128, 64, new[] { 2, 2, 2, 2 }, new[] { 0.1, 0.1, 0.1, 0.1 }, label, failures);
        if (failures.Count != 0) { Console.Error.WriteLine("FAR_WEATHER_ATLAS_GPU_PARITY=FAIL " + string.Join(" | ", failures)); return 1; }
        return 0;
    }

    private void Check(string name, byte[] cpu, byte[] gpu, int w, int h, int[] maxTol, double[] meanTol, string label, List<string> failures)
    {
        if (gpu == null || gpu.Length != cpu.Length) { failures.Add($"{name}: size cpu={cpu.Length} gpu={gpu?.Length ?? -1}"); return; }
        var max = new int[4]; var sum = new double[4]; var over = new int[4];
        for (int i = 0; i < cpu.Length; i++)
        {
            int c = i & 3, d = Math.Abs(cpu[i] - gpu[i]);
            if (d > max[c]) max[c] = d;
            sum[c] += d;
            if (d > 8) over[c]++;
        }
        int texels = cpu.Length / 4;
        string line = $"PARITY {label} {name} {w}x{h}";
        for (int c = 0; c < 4; c++)
        {
            double mean = sum[c] / texels;
            line += $" ch{c}[max={max[c]} mean={mean:F3} over8={over[c]}]";
            if (max[c] > maxTol[c]) failures.Add($"{name} ch{c} max {max[c]} > {maxTol[c]}");
            if (mean > meanTol[c]) failures.Add($"{name} ch{c} mean {mean:F3} > {meanTol[c]}");
        }
        Console.WriteLine(line);
        Report += line + "\n";
        if (OutputDirectory.Length > 0)
        {
            Directory.CreateDirectory(OutputDirectory);
            Save(Path.Combine(OutputDirectory, $"{label}_{name}_cpu.png"), cpu, w, h);
            Save(Path.Combine(OutputDirectory, $"{label}_{name}_gpu.png"), gpu, w, h);
        }
    }

    private static void Save(string path, byte[] rgba, int w, int h)
    {
        using Image image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        image.SavePng(path);
    }
}
