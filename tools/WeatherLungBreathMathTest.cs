using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Weather Lung breath-math contract (S2): the loop closes, gills stay closed on the inhale, the turbine never reverses, revolutions per
/// cycle, vane peak timing, nozzle only glows on the exhale, heat grid off in daylight, ram kinematics finite.
/// Prints WEATHER_LUNG_BREATH_MATH=PASS.
/// </summary>
public partial class WeatherLungBreathMathTest : RefCounted
{
    private const double Eps = 1e-9;
    private const double Tau = Math.PI * 2.0;

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        const double lag = WeatherLungBreathMath.DefaultRippleLag;
        const double va = 35.0, ga = 35.0;
        int revs = WeatherLungBreathMath.TurbineRevolutions(1.5, 180.0);

        Check(revs == 3, $"revs {revs} != 3");
        Check(WeatherLungBreathMath.TurbineRevolutions(0.0, 180.0) == 1, "revs floor is 1");
        Console.WriteLine($"  revs/cycle at defaults = {revs}");

        // Loop closure: every channel at p = 1 equals p = 0 (turbines and rings modulo 2pi).
        double worstClose = 0.0;
        for (int i = 0; i < WeatherLungBreathMath.VaneCount; i++)
            worstClose = Math.Max(worstClose, Math.Abs(WeatherLungBreathMath.Vane(1.0, i, va, lag) - WeatherLungBreathMath.Vane(0.0, i, va, lag)));
        for (int i = 0; i < WeatherLungBreathMath.GillCount; i++)
            worstClose = Math.Max(worstClose, Math.Abs(WeatherLungBreathMath.Gill(1.0, i, ga, lag) - WeatherLungBreathMath.Gill(0.0, i, ga, lag)));
        for (int s = -1; s <= 1; s += 2)
        {
            double d = (WeatherLungBreathMath.Turbine(1.0, s, revs) - WeatherLungBreathMath.Turbine(0.0, s, revs)) / Tau;
            worstClose = Math.Max(worstClose, Math.Abs(d - Math.Round(d)));
            double r = (WeatherLungBreathMath.Ring(1.0, s) - WeatherLungBreathMath.Ring(0.0, s)) / Tau;
            worstClose = Math.Max(worstClose, Math.Abs(r - Math.Round(r)));
        }
        for (int k = 0; k < WeatherLungBreathMath.NozzleCount; k++)
            worstClose = Math.Max(worstClose, Math.Abs(WeatherLungBreathMath.Nozzle(1.0, k) - WeatherLungBreathMath.Nozzle(0.0, k)));
        worstClose = Math.Max(worstClose, Math.Abs(WeatherLungBreathMath.HeatGrid(1.0, 0.0) - WeatherLungBreathMath.HeatGrid(0.0, 0.0)));
        Check(worstClose < 1e-9, $"loop does not close (worst {worstClose:G3})");
        Console.WriteLine($"  loop closure worst error = {worstClose:G3}");

        // Gills exactly 0 for p in [0, 0.5].
        double gillMax = 0.0;
        for (int step = 0; step <= 500; step++)
            for (int g = 0; g < WeatherLungBreathMath.GillCount; g++)
                gillMax = Math.Max(gillMax, WeatherLungBreathMath.Gill(step / 1000.0, g, ga, lag));
        Check(gillMax == 0.0, $"gill nonzero on the inhale ({gillMax})");
        Check(WeatherLungBreathMath.Gill(0.75, 0, ga, lag) > 0.0, "gill 0 should be open at p = 0.75");

        // Turbine strictly increasing (A) and decreasing (B).
        bool increasing = true, decreasing = true;
        double previousA = WeatherLungBreathMath.Turbine(0.0, 1, revs), previousB = WeatherLungBreathMath.Turbine(0.0, -1, revs);
        for (int step = 1; step <= 2000; step++)
        {
            double p = step / 2000.0;
            double a = WeatherLungBreathMath.Turbine(p, 1, revs), b = WeatherLungBreathMath.Turbine(p, -1, revs);
            increasing &= a > previousA;
            decreasing &= b < previousB;
            previousA = a;
            previousB = b;
        }
        Check(increasing, "turbine A not strictly increasing");
        Check(decreasing, "turbine B not strictly decreasing");

        // Vane peak at 0.5 + lag * i.
        double worstPeak = 0.0;
        for (int i = 0; i < WeatherLungBreathMath.VaneCount; i++)
        {
            double best = -1.0, at = 0.0;
            for (int step = 0; step <= 200000; step++)
            {
                double p = step / 200000.0;
                double v = WeatherLungBreathMath.Vane(p, i, va, lag);
                if (v > best) { best = v; at = p; }
            }
            worstPeak = Math.Max(worstPeak, Math.Abs(at - (0.5 + lag * i)));
            Check(Math.Abs(best - va * Math.PI / 180.0) < 1e-9, $"vane {i} peak {best} != amplitude");
        }
        Check(worstPeak < 1e-5, $"vane peak timing off by {worstPeak:G3}");
        Console.WriteLine($"  vane peak timing worst error = {worstPeak:G3}");

        // Nozzle: exactly 0.5 for p in [0, 0.5]; above 0.5 only on the exhale.
        bool inhaleFlat = true, exhaleLit = false;
        for (int step = 0; step <= 1000; step++)
            for (int k = 0; k < WeatherLungBreathMath.NozzleCount; k++)
            {
                double p = step / 1000.0, n = WeatherLungBreathMath.Nozzle(p, k);
                if (p <= 0.5) inhaleFlat &= Math.Abs(n - 0.5) < Eps;
                else exhaleLit |= n > 0.5 + 1e-6;
                Check(n >= 0.5 - Eps && n <= 16.7, $"nozzle {k} out of range {n} at {p}");
            }
        Check(inhaleFlat, "nozzle glows on the inhale");
        Check(exhaleLit, "nozzle never glows on the exhale");

        // Heat grid 0 at sun = 1; positive at night and peaking with the inhale level.
        for (int step = 0; step <= 100; step++) Check(WeatherLungBreathMath.HeatGrid(step / 100.0, 1.0) == 0.0, "heat grid not 0 at sun = 1");
        Check(WeatherLungBreathMath.HeatGrid(0.5, 0.0) > WeatherLungBreathMath.HeatGrid(0.0, 0.0), "heat grid should peak with the inhale level");
        Check(Math.Abs(WeatherLungBreathMath.HeatGrid(0.0, 0.0) - 2.2 * 0.35) < Eps, "heat grid at p = 0 at night");

        // Heliostat: pi at night, th1 at day.
        Check(Math.Abs(WeatherLungBreathMath.Heliostat(0.0, 1.1263) - Math.PI) < Eps, "heliostat night != pi");
        Check(Math.Abs(WeatherLungBreathMath.Heliostat(1.0, 1.1263) - 1.1263) < Eps, "heliostat day != th1");

        // Phase wrap.
        double p0 = WeatherLungBreathMath.Phase(100.0, 0.0, 180.0, 0.25);
        Check(p0 >= 0.0 && p0 < 1.0, "phase out of range");
        Check(Math.Abs(WeatherLungBreathMath.Phase(-45.0, 0.0, 180.0, 0.0) - 0.75) < Eps, "negative clock wraps");

        // Ram kinematics: finite and responsive.
        var pivot = new Vector3(-750f, 0f, 0f);
        var pin = new Vector3(777f, 1000f, 0f);
        var baseB = new Vector3(-250f, 20f, 0f);
        WeatherLungBreathMath.Ram(0.0, pivot, pin, baseB, 1100.0, out double psi0, out double rod0);
        WeatherLungBreathMath.Ram(0.4, pivot, pin, baseB, 1100.0, out double psi1, out double rod1);
        Check(double.IsFinite(psi0) && double.IsFinite(rod0) && double.IsFinite(psi1) && double.IsFinite(rod1), "ram not finite");
        Check(Math.Abs(psi1 - psi0) > 1e-3, "ram angle does not respond to the vane");

        if (failures.Count != 0) { Console.Error.WriteLine("WEATHER_LUNG_BREATH_MATH=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("WEATHER_LUNG_BREATH_MATH=PASS");
        return 0;
    }
}
