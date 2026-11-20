using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Measures, from the real FFT provider's rendered displacement data, which way the waves travel
/// relative to the configured wind direction, for both values of SpectrumTravelOffsetDegrees.
/// Frame: texture x = around, texture row index (y) = across (uv.y grows with across).
/// </summary>
public partial class WaterFftTravelGpuSmoke : Node
{
    private const int Resolution = 128;
    private const double T0 = 100d, Dt = .25d;
    private const int MaxShift = 16;
    private static readonly float[] Offsets = { 0f, 180f };
    private static readonly float[] Winds = { 0f, 90f, 200f };

    private RingWaterFftProvider? _provider;
    private int _caseIndex, _frames, _stageFrames;
    private string _stage = "start";
    private long _baseCompleted;
    private volatile byte[]? _readback;
    private float[]? _h0;
    private readonly List<(float offset, float wind, float travel, float axis, float diff)> _results = new();
    private readonly StringBuilder _lines = new();

    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public string LastFailure { get; private set; } = string.Empty;

    public override void _Ready()
    {
        if (RenderingServer.GetRenderingDevice() is null) Fail("unsupported GPU renderer (no main RenderingDevice)");
    }

    public override void _Process(double delta)
    {
        if (Completed) return;
        try
        {
            if (++_stageFrames > 600) { Fail($"timeout at case {_caseIndex} stage {_stage}"); return; }
            _frames++;
            float offset = Offsets[_caseIndex / Winds.Length], wind = Winds[_caseIndex % Winds.Length];
            switch (_stage)
            {
                case "start":
                    RingWaterFftProvider.SpectrumTravelOffsetDegrees = offset;
                    _provider?.Dispose();
                    _provider = new RingWaterFftProvider();
                    _provider.Configure(new RingWaterFftSettings(Resolution, 1, 12017, new Vector2(64, 64), new Vector2(64, 64), 10f, wind, 3, .5f));
                    Next("t0");
                    break;
                case "t0":
                    _provider!.RequestUpdate(T0, 1);
                    if (_provider.Ready && _provider.CompletedUpdates >= 1) { _baseCompleted = _provider.CompletedUpdates; _readback = null; _provider.RequestDisplacementReadbackForTests(b => _readback = b); Next("read0"); }
                    break;
                case "read0":
                    if (_readback is null) break;
                    _h0 = Height(_readback, "t0");
                    _provider!.RequestUpdate(T0 + Dt, 1);
                    Next("t1");
                    break;
                case "t1":
                    _provider!.RequestUpdate(T0 + Dt, 1);
                    if (_provider.CompletedUpdates > _baseCompleted) { _readback = null; _provider.RequestDisplacementReadbackForTests(b => _readback = b); Next("read1"); }
                    break;
                case "read1":
                    if (_readback is null) break;
                    FinishCase(offset, wind, _h0!, Height(_readback, "t1"));
                    if (++_caseIndex >= Offsets.Length * Winds.Length) Conclude(); else Next("start");
                    break;
            }
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private static float[] Height(byte[] data, string what)
    {
        int n = Resolution;
        if (data.Length < n * n * 8) throw new InvalidOperationException($"{what}: readback too small ({data.Length} bytes)");
        float[] h = new float[n * n];
        for (int i = 0; i < n * n; i++) h[i] = (float)BitConverter.ToHalf(data, i * 8 + 2); // channel G = height
        return h;
    }

    private void FinishCase(float offset, float wind, float[] a, float[] b)
    {
        int n = Resolution;
        // Find (dx,dy) minimising mean squared difference of b(x,y) against a(x-dx, y-dy), periodic.
        int side = 2 * MaxShift + 1;
        double[,] cost = new double[side, side];
        int bestX = 0, bestY = 0; double best = double.MaxValue;
        for (int dy = -MaxShift; dy <= MaxShift; dy++)
            for (int dx = -MaxShift; dx <= MaxShift; dx++)
            {
                double sum = 0;
                for (int y = 0; y < n; y++)
                {
                    int sy = ((y - dy) % n + n) % n;
                    for (int x = 0; x < n; x++)
                    {
                        int sx = ((x - dx) % n + n) % n;
                        double d = b[y * n + x] - a[sy * n + sx]; sum += d * d;
                    }
                }
                cost[dy + MaxShift, dx + MaxShift] = sum;
                if (sum < best) { best = sum; bestX = dx; bestY = dy; }
            }
        double fx = bestX + Parabola(bestX, bestY, true, cost), fy = bestY + Parabola(bestX, bestY, false, cost);
        float travel = Mathf.RadToDeg(Mathf.Atan2((float)fy, (float)fx));
        float axis = SpectrumAxisDegrees(a);
        float diff = Wrap(travel - wind);
        _results.Add((offset, wind, travel, axis, diff));
        string line = $"WATER_FFT_TRAVEL case offset={offset:0} wind={wind:0} shift=({fx:0.00},{fy:0.00}) travel_deg={travel:0.0} spectrum_axis_deg={axis:0.0} diff_to_wind={diff:0.0} (int_shift=({bestX},{bestY}))";
        GD.Print(line);
        if (Math.Abs(bestX) == MaxShift || Math.Abs(bestY) == MaxShift) GD.Print("WATER_FFT_TRAVEL warning shift at search limit, estimate ambiguous");
    }

    private static double Parabola(int bx, int by, bool xAxis, double[,] c)
    {
        int ix = bx + MaxShift, iy = by + MaxShift;
        int i = xAxis ? ix : iy;
        if (i <= 0 || i >= 2 * MaxShift) return 0;
        double m = xAxis ? c[iy, ix - 1] : c[iy - 1, ix], z = c[iy, ix], p = xAxis ? c[iy, ix + 1] : c[iy + 1, ix];
        double den = m - 2 * z + p;
        return Math.Abs(den) < 1e-12 ? 0 : .5 * (m - p) / den;
    }

    // Energy-weighted dominant axis of |FFT(h)|^2, via the doubled-angle mean (a wave axis is mod 180 degrees).
    private static float SpectrumAxisDegrees(float[] h)
    {
        int n = Resolution;
        double[] re = new double[n * n], im = new double[n * n];
        double[] cos = new double[n], sin = new double[n];
        for (int i = 0; i < n; i++) { cos[i] = Math.Cos(2 * Math.PI * i / n); sin[i] = -Math.Sin(2 * Math.PI * i / n); }
        // rows (x direction)
        for (int y = 0; y < n; y++)
            for (int kx = 0; kx < n; kx++)
            {
                double r = 0, m = 0;
                for (int x = 0; x < n; x++) { int idx = (kx * x) % n; r += h[y * n + x] * cos[idx]; m += h[y * n + x] * sin[idx]; }
                re[y * n + kx] = r; im[y * n + kx] = m;
            }
        double sc = 0, ss = 0;
        for (int kx = 0; kx < n; kx++)
            for (int ky = 0; ky < n; ky++)
            {
                double r = 0, m = 0;
                for (int y = 0; y < n; y++)
                {
                    int idx = (ky * y) % n; double c = cos[idx], s = sin[idx];
                    double a = re[y * n + kx], b = im[y * n + kx];
                    r += a * c - b * s; m += a * s + b * c;
                }
                double e = r * r + m * m;
                int wx = kx <= n / 2 ? kx : kx - n, wy = ky <= n / 2 ? ky : ky - n;
                if (wx == 0 && wy == 0) continue;
                double th = Math.Atan2(wy, wx);
                sc += e * Math.Cos(2 * th); ss += e * Math.Sin(2 * th);
            }
        return (float)(.5 * Math.Atan2(ss, sc) * 180d / Math.PI);
    }

    private static float Wrap(float d) { while (d > 180f) d -= 360f; while (d <= -180f) d += 360f; return d; }

    private void Conclude()
    {
        int? downwind = null;
        foreach (float offset in Offsets)
        {
            bool ok = true;
            foreach (var r in _results) if (r.offset == offset && Math.Abs(r.diff) >= 35f) ok = false;
            if (ok) { downwind = (int)offset; break; }
        }
        RingWaterFftProvider.SpectrumTravelOffsetDegrees = RingWaterFftProvider.ShippedSpectrumTravelOffsetDegrees;
        _provider?.Dispose(); _provider = null;
        Completed = true;
        if (downwind is int o && o == (int)RingWaterFftProvider.ShippedSpectrumTravelOffsetDegrees) { Passed = true; GD.Print($"WATER_FFT_TRAVEL=PASS downwind_offset={o} (shipped)"); }
        else
        {
            LastFailure = downwind is int other ? $"the shipped offset {RingWaterFftProvider.ShippedSpectrumTravelOffsetDegrees} is not downwind (offset {other} is)"
                : "no offset gives travel within 35 degrees of wind for all cases";
            GD.PushError("WATER_FFT_TRAVEL=FAIL " + LastFailure);
        }
    }

    private void Next(string stage) { _stage = stage; _stageFrames = 0; }
    private void Fail(string reason)
    {
        if (Completed) return;
        RingWaterFftProvider.SpectrumTravelOffsetDegrees = RingWaterFftProvider.ShippedSpectrumTravelOffsetDegrees;
        LastFailure = reason; Completed = true; Passed = false;
        GD.PushError("WATER_FFT_TRAVEL=FAIL " + reason);
        _provider?.Dispose(); _provider = null;
    }
    public override void _ExitTree() { RingWaterFftProvider.SpectrumTravelOffsetDegrees = RingWaterFftProvider.ShippedSpectrumTravelOffsetDegrees; _provider?.Dispose(); _provider = null; }
}
