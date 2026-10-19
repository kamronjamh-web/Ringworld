using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Fixture-only per-pixel temporal luminance statistics for tools/compositor_temporal_stability_test.gd. Frames are folded into a
/// running mean / variance (Welford) so 32 full-resolution captures never have to be held at once.
/// </summary>
public partial class CompositorStabilityHelper : RefCounted
{
    private int _width;
    private int _height;
    private int _count;
    private float[] _mean = Array.Empty<float>();
    private float[] _m2 = Array.Empty<float>();

    public void Begin(int width, int height)
    {
        _width = width;
        _height = height;
        _count = 0;
        _mean = new float[width * height];
        _m2 = new float[width * height];
    }

    /// <summary>Folds one RGBA8 capture (Image.FORMAT_RGBA8 data) into the per-pixel luminance statistics.</summary>
    public bool Accumulate(byte[] rgba8)
    {
        int pixels = _width * _height;
        if (rgba8.Length < pixels * 4) return false;
        _count++;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            float luminance = (0.2126f * rgba8[o] + 0.7152f * rgba8[o + 1] + 0.0722f * rgba8[o + 2]) / 255f;
            float delta = luminance - _mean[i];
            _mean[i] += delta / _count;
            _m2[i] += delta * (luminance - _mean[i]);
        }
        return true;
    }

    /// <summary>
    /// Returns {roi: {mean, p99, pixels}} for whole / edge / flat / horizon and optionally saves the per-pixel standard deviation
    /// (scaled x16, clamped) as a greyscale PNG.
    /// </summary>
    public Godot.Collections.Dictionary Finish(string stddevPngPath)
    {
        int pixels = _width * _height;
        var result = new Godot.Collections.Dictionary();
        if (_count < 2 || pixels == 0) return result;
        var stddev = new float[pixels];
        for (int i = 0; i < pixels; i++) stddev[i] = MathF.Sqrt(Math.Max(_m2[i] / (_count - 1), 0f));

        // Sobel magnitude of the temporal mean image locates geometric / cloud edges independent of the noise being measured.
        var sobel = new float[pixels];
        var rowGradient = new double[_height];
        for (int y = 1; y < _height - 1; y++)
        {
            for (int x = 1; x < _width - 1; x++)
            {
                float a = _mean[(y - 1) * _width + x - 1], b = _mean[(y - 1) * _width + x], c = _mean[(y - 1) * _width + x + 1];
                float d = _mean[y * _width + x - 1], f = _mean[y * _width + x + 1];
                float g = _mean[(y + 1) * _width + x - 1], h = _mean[(y + 1) * _width + x], k = _mean[(y + 1) * _width + x + 1];
                float gx = (c + 2f * f + k) - (a + 2f * d + g);
                float gy = (g + 2f * h + k) - (a + 2f * b + c);
                sobel[y * _width + x] = MathF.Sqrt(gx * gx + gy * gy);
                rowGradient[y] += MathF.Abs(gy);
            }
        }
        var sortedSobel = (float[])sobel.Clone();
        Array.Sort(sortedSobel);
        float edgeThreshold = sortedSobel[(int)(0.95 * (pixels - 1))];
        float flatThreshold = sortedSobel[pixels / 2];

        int horizonRow = 1;
        for (int y = 2; y < _height - 1; y++) if (rowGradient[y] > rowGradient[horizonRow]) horizonRow = y;
        int band = Math.Max(1, (int)(0.05 * _height));
        int bandStart = Math.Max(0, horizonRow - band);
        int bandEnd = Math.Min(_height - 1, horizonRow + band);

        var whole = new List<float>(pixels);
        var edge = new List<float>();
        var flat = new List<float>();
        var horizon = new List<float>();
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                int i = y * _width + x;
                whole.Add(stddev[i]);
                if (sobel[i] >= edgeThreshold && edgeThreshold > 0f) edge.Add(stddev[i]);
                if (sobel[i] < flatThreshold) flat.Add(stddev[i]);
                if (y >= bandStart && y <= bandEnd) horizon.Add(stddev[i]);
            }
        }
        result["whole"] = Summarize(whole);
        result["edge"] = Summarize(edge);
        result["flat"] = Summarize(flat);
        result["horizon"] = Summarize(horizon);
        result["horizon_row"] = horizonRow;

        if (!string.IsNullOrEmpty(stddevPngPath))
        {
            var bytes = new byte[pixels];
            for (int i = 0; i < pixels; i++) bytes[i] = (byte)Math.Clamp((int)(stddev[i] * 16f * 255f), 0, 255);
            Image.CreateFromData(_width, _height, false, Image.Format.L8, bytes).SavePng(stddevPngPath);
        }
        return result;
    }

    private static Godot.Collections.Dictionary Summarize(List<float> values)
    {
        var summary = new Godot.Collections.Dictionary { ["pixels"] = values.Count, ["mean"] = 0.0, ["p99"] = 0.0 };
        if (values.Count == 0) return summary;
        double sum = 0;
        foreach (float value in values) sum += value;
        values.Sort();
        summary["mean"] = sum / values.Count;
        summary["p99"] = values[(int)(0.99 * (values.Count - 1))];
        return summary;
    }
}
