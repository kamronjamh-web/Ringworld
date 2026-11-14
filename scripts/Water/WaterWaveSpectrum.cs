using System;

namespace RingworldTechDemo.Water;

/// <summary>
/// T3R-10d (docs/work-orders/terrain-vegetation/T3.md, V10 and the primary decision of 2026-11-14): the significant wave height the sea shows, derived on the CPU from the same JONSWAP/TMA spectrum the FFT
/// compute shader builds (third_party/godot_ocean_waves/compute/spectrum_compute.glsl, with the constants RingWaterFftProvider.DispatchCascade pushes: depth 20 m, swell 0.8, detail 1, spread 0.2).
/// The shader's grid sum of s * d * w_norm over k is the integral of S(w) D(theta) dw dtheta, the variance of the height of that cascade; here it is integrated on a polar grid (log-spaced k, uniform theta)
/// so that the cost does not depend on the FFT resolution. The geometry layers add, as the displacements the surface shows do (ring_fft_geometry_displacement sums cascades 1 to count-1, and cascade 0 only when it is the single cascade; cascade 0 is a normal-only micro-detail layer); Hs = 4 sqrt(m0) times the geometry height scale. Pure arithmetic, no node.
/// </summary>
public static class WaterWaveSpectrum
{
    private const double G = 9.81, Depth = 20.0, Swell = 0.8, Spread = 0.2;
    private const int KBins = 96, ThetaBins = 72;

    /// <summary>How many times the shader's sum of s * d * w_norm the FFT really draws as height variance (the complex amplitudes are two unit normals times sqrt(2 s d w_norm), and h(k) adds h0(k) and the conjugate of h0(-k): 2 x 2 x 2 = 8; measured by water_wave_spectrum_gpu_test, median ratio 8.3 over three winds and four cascades).</summary>
    public const double DrawnVarianceFactor = 8.0;

    /// <summary>The significant wave height in metres of the displaced surface: Hs = 4 sqrt(sum of the geometry cascades' variances) * heightScale (cascade 0 is skipped when there are several, as the shader does). Zero for no wind or no cascade.</summary>
    /// <param name="resolution">The FFT grid size (128, 256, 512 or 1024); it sets the highest wavenumber of each cascade.</param>
    /// <param name="periods">The tile length in metres of each cascade (the X period; the tiles are square).</param>
    public static double SignificantHeight(int resolution, ReadOnlySpan<double> periods, double windSpeed, double heightScale)
    {
        if (!(windSpeed > 0.0) || resolution < 2 || !(heightScale > 0.0)) return 0.0;
        double variance = 0.0;
        for (int c = FirstGeometryCascade(periods.Length); c < periods.Length; c++) variance += Variance(resolution, periods[c], windSpeed);
        return 4.0 * Math.Sqrt(variance * DrawnVarianceFactor) * heightScale;
    }

    /// <summary>The first cascade the displaced geometry sums (ring_fft_geometry_displacement): 1 when there are several cascades, else 0.</summary>
    public static int FirstGeometryCascade(int cascadeCount) => cascadeCount > 1 ? 1 : 0;

    /// <summary>The height variance of one cascade in square metres.</summary>
    public static double Variance(int resolution, double period, double windSpeed)
    {
        if (!(period > 0.0) || !(windSpeed > 0.0)) return 0.0;
        double fetch = Math.Max(1000.0, period * period * 0.5);
        double alpha = 0.076 * Math.Pow(windSpeed * windSpeed / (fetch * G), 0.22);
        double peak = 22.0 * Math.Pow(G * G / (windSpeed * fetch), 1.0 / 3.0);
        double kMin = 2.0 * Math.PI / period, kMax = Math.PI * resolution / period;
        if (!(kMax > kMin)) return 0.0;
        double lnMin = Math.Log(kMin), dLn = (Math.Log(kMax) - lnMin) / KBins, dTheta = 2.0 * Math.PI / ThetaBins;
        double sum = 0.0;
        for (int i = 0; i < KBins; i++)
        {
            double k = Math.Exp(lnMin + (i + 0.5) * dLn);
            double dk = k * dLn;
            double a = k * Depth, b = Math.Tanh(a);
            double w = Math.Sqrt(G * k * b);
            double dwdk = 0.5 * G * (b + a * (1.0 - b * b)) / w;
            double s = Tma(w, peak, alpha);
            double ring = 0.0;
            for (int j = 0; j < ThetaBins; j++)
            {
                double theta = -Math.PI + (j + 0.5) * dTheta;
                ring += (0.5 / Math.PI) * (Spread) + (1.0 - Spread) * Hasselmann(w, peak, windSpeed, theta);
            }
            sum += s * ring * dTheta * dwdk * dk;
        }
        return sum;
    }

    private static double Tma(double w, double wp, double alpha)
    {
        const double Beta = 1.25, Gamma = 3.3;
        double sigma = w <= wp ? 0.07 : 0.09;
        double r = Math.Exp(-(w - wp) * (w - wp) / (2.0 * sigma * sigma * wp * wp));
        double jonswap = alpha * G * G / Math.Pow(w, 5.0) * Math.Exp(-Beta * Math.Pow(wp / w, 4.0)) * Math.Pow(Gamma, r);
        double wh = Math.Min(w * Math.Sqrt(Depth / G), 2.0);
        double attenuation = wh <= 1.0 ? 0.5 * wh * wh : 1.0 - 0.5 * (2.0 - wh) * (2.0 - wh);
        return jonswap * attenuation;
    }

    private static double Hasselmann(double w, double wp, double windSpeed, double theta)
    {
        double p = w / wp;
        double s = w <= wp ? 6.97 * Math.Pow(Math.Abs(p), 4.06) : 9.77 * Math.Pow(Math.Abs(p), -2.33 - 1.45 * (windSpeed * wp / G - 1.17));
        double sXi = 16.0 * Math.Tanh(wp / w) * Swell * Swell;
        double sTotal = s + sXi;
        return LonguetHigginsNormalization(sTotal) * Math.Pow(Math.Abs(Math.Cos(theta * 0.5)), 2.0 * sTotal);
    }

    private static double LonguetHigginsNormalization(double s)
    {
        double a = Math.Sqrt(s);
        return s < 0.4 ? 0.5 / Math.PI + s * (0.220636 + s * (-0.109 + s * 0.090)) : 1.0 / Math.Sqrt(Math.PI) * (a * 0.5 + 1.0 / a * 0.0625);
    }
}
