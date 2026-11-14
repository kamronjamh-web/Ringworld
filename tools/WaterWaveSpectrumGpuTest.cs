using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Water;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-10d (docs/work-orders/terrain-vegetation/T3.md, V10): pins the CPU significant wave height (WaterWaveSpectrum) to the surface the FFT really draws. For three winds, the provider runs 4 cascades
/// at 128 x 128; the displacement of every cascade is read back and its height variance (channel Y, metres) is compared with WaterWaveSpectrum.Variance for the same period and wind. The ratio
/// is printed per cascade; the check is that the median ratio is within 25 % of WaterWaveSpectrum's calibration, and that the Hs WaterWaveSpectrum.SignificantHeight returns for 2 and for 4 cascades is within 0.8 to 1.25 of the Hs of the drawn geometry variances (cascades 1..count-1). Prints WATER_WAVE_SPECTRUM_GPU=PASS|FAIL. GPU: tools/run_tests.ps1 -Tests water_wave_spectrum_gpu_test.
/// </summary>
public partial class WaterWaveSpectrumGpuTest : Node
{
    private static readonly double[] Periods = { 12, 79, 316, 1264 };
    private static readonly float[] Winds = { 3f, 6f, 10f };
    private RingWaterFftProvider? _provider;
    private int _frame, _stageFrames, _wind, _layer;
    private long _updatesAtStage;
    private bool _pending;
    private readonly double[] _observed = new double[4];
    private readonly List<string> _log = new();
    private readonly List<double> _ratios = new();
    private readonly List<double> _hsRatios = new();
    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public string LastFailure { get; private set; } = "";

    public override void _Ready()
    {
        if (RenderingServer.GetRenderingDevice() is null) { Fail("no main RenderingDevice"); return; }
        Begin();
    }

    private void Begin()
    {
        _provider?.Dispose();
        _provider = new RingWaterFftProvider();
        _provider.Configure(new RingWaterFftSettings(128, 4, 12017, new Vector2(12, 12), new Vector2(79, 79), Winds[_wind], 20, 3, .5f, new Vector2(316, 316), new Vector2(1264, 1264)));
        _stageFrames = 0; _layer = 0; _pending = false; _updatesAtStage = _provider.CompletedUpdates;
    }

    public override void _Process(double delta)
    {
        if (Completed) return;
        if (++_frame > 2400) { Fail("timeout"); return; }
        if (_provider == null) return;
        _provider.RequestUpdate(_frame / 60d, Mathf.Clamp((float)delta, .001f, .05f));
        if (!_provider.Ready || _pending) return;
        if (++_stageFrames < 24 || _provider.CompletedUpdates <= _updatesAtStage) return;
        _pending = true;
        int layer = _layer;
        _provider.RequestDisplacementReadbackForTests(bytes =>
        {
            try { Consume(layer, bytes); } catch (Exception e) { Fail(e.Message); }
            finally { _pending = false; }
        }, layer, 0);
    }

    private void Consume(int layer, byte[] bytes)
    {
        int n = 128;
        if (bytes.Length != n * n * 8) throw new InvalidOperationException($"displacement readback is {bytes.Length} bytes");
        double sum = 0, sum2 = 0;
        for (int i = 0; i < n * n; i++)
        {
            double y = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i * 8 + 2));
            sum += y; sum2 += y * y;
        }
        double mean = sum / (n * n);
        _observed[layer] = sum2 / (n * n) - mean * mean;
        if (++_layer < 4) return;
        double wind = Winds[_wind];
        for (int c = 0; c < 4; c++)
        {
            double predicted = WaterWaveSpectrum.Variance(128, Periods[c], wind);
            double ratio = _observed[c] / predicted;
            _ratios.Add(ratio);
            _log.Add($"wind {wind} m/s cascade {c} (period {Periods[c]} m): drawn height variance {_observed[c]:0.0000} m^2, spectrum {predicted:0.0000} m^2, ratio {ratio:0.###}");
        }
        // The sea the player sees: the geometry sums cascades 1..count-1 (cascade 0 only when alone). Compare the Hs of the drawn variances with the CPU Hs for 4 cascades and for the default 2.
        foreach (int count in new[] { 2, 4 })
        {
            double drawn = 0;
            for (int c = WaterWaveSpectrum.FirstGeometryCascade(count); c < count; c++) drawn += _observed[c];
            double drawnHs = 4.0 * Math.Sqrt(drawn), cpuHs = WaterWaveSpectrum.SignificantHeight(128, Periods.AsSpan(0, count), wind, 1.0), hsRatio = cpuHs / drawnHs;
            _hsRatios.Add(hsRatio);
            _log.Add($"wind {wind} m/s, {count} cascades: drawn Hs {drawnHs:0.00} m, WaterWaveSpectrum.SignificantHeight {cpuHs:0.00} m, ratio {hsRatio:0.###}");
        }
        if (++_wind < Winds.Length) { Begin(); return; }
        Finish();
    }

    private void Finish()
    {
        foreach (string line in _log) Console.WriteLine("WATER_WAVE_SPECTRUM_GPU_DETAIL " + line);
        double lo = double.MaxValue, hi = 0;
        foreach (double r in _ratios) { lo = Math.Min(lo, r); hi = Math.Max(hi, r); }
        // One realisation of the random spectrum per cascade: a single field carries sampling noise (a few modes in the long cascades), so the band is wide.
        double calibration = WaterWaveSpectrum.DrawnVarianceFactor;
        Console.WriteLine($"WATER_WAVE_SPECTRUM_GPU_DETAIL ratios {lo:0.###} to {hi:0.###}; the CPU spectrum uses a drawn-variance factor of {calibration.ToString("0.###", CultureInfo.InvariantCulture)}");
        _ratios.Sort();
        double median = _ratios[_ratios.Count / 2];
        if (Math.Abs(median / calibration - 1.0) > 0.25) { Fail($"median ratio {median:0.###} is not within 25 % of the factor {calibration:0.###}"); return; }
        foreach (double r in _hsRatios)
            if (r < 0.8 || r > 1.25) { Fail($"CPU Hs is {r:0.###} of the drawn Hs (allowed 0.8 to 1.25)"); return; }
        Passed = true; Completed = true;
        Console.WriteLine("WATER_WAVE_SPECTRUM_GPU=PASS");
        _provider?.Dispose(); _provider = null;
    }

    private void Fail(string reason)
    {
        if (Completed) return;
        LastFailure = reason; Completed = true; Passed = false;
        GD.PushError("WATER_WAVE_SPECTRUM_GPU=FAIL " + reason);
        _provider?.Dispose(); _provider = null;
    }

    public override void _ExitTree() { _provider?.Dispose(); _provider = null; }
}
