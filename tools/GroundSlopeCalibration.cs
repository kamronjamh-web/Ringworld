using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-5: headless calibration of GROUND_SLOPE_BIAS_PER_OCTAVE_DEG (T4 design D2). At the T0 mountain and breaks anchors of the pinned world (LandscapeWorld, seed 1337) it
/// measures the slope the way a tier's normal is differenced (central differences over the baseline, per axis), then compares the mean rock exposure at a coarse
/// baseline (with that baseline's slope bias) against the mean at the 4 m reference. Heights are the shaped ground model's, the same route the proxy builders sample.
/// </summary>
public static class GroundSlopeCalibration
{
    public static readonly double[] Baselines = { 4, 8, 16, 32, 64, 128 };
    public const double PatchHalfMeters = 384.0;
    public const double StrideMeters = 16.0;

    /// <summary>The T0 anchors per class (landscape_poses_v8_s1337.json and landscape_poses_dressing_s1337.json): around, across.</summary>
    public static readonly (string Name, (double Around, double Across)[] Anchors)[] Classes =
    {
        ("Mountain", new[] { (157672.94733047034, 301532.3223304703), (31414526.125, 187500.0), (31415472.485389695, 190163.6396103068), (363.7646103067893, 219336.3603896932), (0.125, 189000.0) }),
        ("Breaks", new[] { (157078.125, 219000.0) }),
    };

    /// <summary>Slopes in degrees per class and baseline: result[class][baseline][sample].</summary>
    public static double[][][] MeasureSlopes()
    {
        using var model = LandscapeWorld.CreateShapedModel();
        int n = (int)(2.0 * PatchHalfMeters / StrideMeters);
        var result = new double[Classes.Length][][];
        for (int c = 0; c < Classes.Length; c++)
        {
            var anchors = Classes[c].Anchors;
            result[c] = new double[Baselines.Length][];
            for (int b = 0; b < Baselines.Length; b++) result[c][b] = new double[anchors.Length * n * n];
            int index = 0;
            foreach (var (around0, across0) in anchors)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++, index++)
                    {
                        double around = around0 - PatchHalfMeters + (i + 0.5) * StrideMeters;
                        double across = across0 - PatchHalfMeters + (j + 0.5) * StrideMeters;
                        for (int b = 0; b < Baselines.Length; b++)
                        {
                            double h = Baselines[b] * 0.5;
                            double gx = (model.CalculateHeight(around + h, across) - model.CalculateHeight(around - h, across)) / Baselines[b];
                            double gz = (model.CalculateHeight(around, across + h) - model.CalculateHeight(around, across - h)) / Baselines[b];
                            result[c][b][index] = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180.0 / Math.PI;
                        }
                    }
        }
        return result;
    }

    /// <summary>Mean rock exposure (relief 0, forest 0, default rock start) per class and baseline for a bias constant.</summary>
    public static double[][] MeanExposure(double[][][] slopes, double biasPerOctaveDeg)
    {
        var means = new double[slopes.Length][];
        for (int c = 0; c < slopes.Length; c++)
        {
            means[c] = new double[Baselines.Length];
            for (int b = 0; b < Baselines.Length; b++)
            {
                double bias = RingGroundSurfaceModel.SlopeBiasDegrees(Baselines[b], biasPerOctaveDeg);
                double sum = 0.0;
                foreach (double slope in slopes[c][b])
                    sum += RingGroundSurfaceModel.SmoothStep(RingGroundSurfaceModel.DefaultRockStartDeg, RingGroundSurfaceModel.DefaultRockStartDeg + RingGroundSurfaceModel.RockRampDeg, slope + bias);
                means[c][b] = sum / slopes[c][b].Length;
            }
        }
        return means;
    }

    /// <summary>Worst relative deviation of a coarse baseline from the 4 m mean. Breaks are only judged up to 32 m: their 40 m terraces vanish from a 64 m or coarser normal, which no bias recovers (T4-5 finding).</summary>
    public static double WorstDeviation(double[][] means)
    {
        double worst = 0.0;
        for (int c = 0; c < means.Length; c++)
        {
            double[] row = means[c];
            int last = Classes[c].Name == "Breaks" ? Array.IndexOf(Baselines, 32.0) : row.Length - 1;
            for (int b = 1; b <= last; b++) worst = Math.Max(worst, Math.Abs(row[b] - row[0]) / Math.Max(row[0], 1e-9));
        }
        return worst;
    }
}
