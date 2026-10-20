using System;
using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// The Weather Lung breath driver functions, reproduced exactly from the kit's build_weather_lung.py (K9 of the integration plan).
/// Pure, static and double precision. <c>p</c> is the breath phase in [0, 1]: inhale 0..0.5, exhale 0.5..1. All angles are radians
/// about the pivot's stored axis (<c>wl_axis</c>); the GLB rest pose is NOT phase 0, so callers write absolute poses.
/// </summary>
public static class WeatherLungBreathMath
{
    public const double DefaultVaneAmplitudeDeg = 35.0;
    public const double DefaultGillAmplitudeDeg = 35.0;
    public const double DefaultRippleLag = 0.012;
    public const double DefaultTurbineMaxRpm = 1.5;
    public const double DefaultCycleSeconds = 180.0;
    public const int VaneCount = 5;
    public const int GillCount = 16;
    public const int NozzleCount = 12;
    private const double Tau = Math.PI * 2.0;
    /// <summary>The kit's reference speed: revolutions per cycle = max(1, round(maxRpm / 1.5 * cycle / 60)).</summary>
    private const double ReferenceRpm = 1.5;
    private const double GillWindow = 0.38;

    public static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Phase from a monotonic clock: frac((clock - t0) / cycle + phi0).</summary>
    public static double Phase(double clockSeconds, double t0, double cycleSeconds, double phi0)
        => Frac((clockSeconds - t0) / Math.Max(1e-6, cycleSeconds) + phi0);

    /// <summary>Intake vane i opening: rad(amp) * (0.5 - 0.5 cos 2pi (p - lag i)). Closed at 0, peak at p = 0.5 + lag i.</summary>
    public static double Vane(double p, int index, double amplitudeDeg, double lag)
        => amplitudeDeg * (Math.PI / 180.0) * (0.5 - 0.5 * Math.Cos(Tau * (p - lag * index)));

    /// <summary>Gill gi flare: rad(amp) * sin^2(pi * clamp((p - 0.5 - lag kc) / 0.38, 0, 1)), kc = gi / 2 + 0.5 (gi % 2). Exactly 0 on the inhale.</summary>
    public static double Gill(double p, int index, double amplitudeDeg, double lag)
    {
        double kc = index / 2 + 0.5 * (index % 2);
        double x = Math.Clamp((p - 0.5 - lag * kc) / GillWindow, 0.0, 1.0);
        double s = Math.Sin(Math.PI * x);
        return amplitudeDeg * (Math.PI / 180.0) * s * s;
    }

    /// <summary>Whole turbine revolutions per cycle: max(1, round(maxRpm / 1.5 * cycle / 60)).</summary>
    public static int TurbineRevolutions(double maxRpm, double cycleSeconds)
        => Math.Max(1, (int)Math.Round(maxRpm / ReferenceRpm * cycleSeconds / 60.0, MidpointRounding.ToEven));

    /// <summary>Turbine angle: sign * 2pi * revs * (p + 0.5 (1 - cos 2pi p) / 2pi). Strictly increasing for sign = +1; rotor A +1, B -1.</summary>
    public static double Turbine(double p, int sign, int revolutions)
        => sign * Tau * revolutions * (p + 0.5 * (1.0 - Math.Cos(Tau * p)) / Tau);

    /// <summary>Guide ring angle: sign * 2pi * p (A +1, B -1), about the arch-plane normal.</summary>
    public static double Ring(double p, int sign) => sign * Tau * p;

    /// <summary>Exhale envelope max(0, -sin 2pi p)^2: 0 for p in [0, 0.5], peaks at p = 0.75.</summary>
    public static double ExhaleEnvelope(double p)
    {
        double e = Math.Max(0.0, -Math.Sin(Tau * p));
        return e * e;
    }

    /// <summary>Vane opening as a 0..1 fraction (the inhale level): 0.5 - 0.5 cos 2pi p.</summary>
    public static double InhaleLevel(double p) => 0.5 - 0.5 * Math.Cos(Tau * p);

    /// <summary>Nozzle k glow factor, exactly0.5 + 16 max(0,-sin 2pi p)^2 (0.2 + 0.8 max(0, cos(4pi (k/12 - 2 (p - 0.5))))^3).</summary>
    public static double Nozzle(double p, int k)
    {
        double e = Math.Max(0.0, -Math.Sin(Tau * p));
        double c = Math.Max(0.0, Math.Cos(2.0 * Tau * (k / (double)NozzleCount - 2.0 * (p - 0.5))));
        return 0.5 + 16.0 * e * e * (0.2 + 0.8 * c * c * c);
    }

    /// <summary>Heliostat angle: pi + (th1 - pi) * smoothstep(sun). Stowed face-down (pi) at night, tracking th1 in daylight.</summary>
    public static double Heliostat(double sun, double trackAngle)
    {
        double s = Math.Clamp(sun, 0.0, 1.0);
        return Math.PI + (trackAngle - Math.PI) * (s * s * (3.0 - 2.0 * s));
    }

    /// <summary>Heat exchanger grid glow: 2.2 (1 - sun) (0.35 + 0.65 (0.5 - 0.5 cos 2pi p)). Zero at sun = 1.</summary>
    public static double HeatGrid(double p, double sun)
        => 2.2 * (1.0 - Math.Clamp(sun, 0.0, 1.0)) * (0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(Tau * p)));

    /// <summary>
    /// Ram kinematics (K8). The vane rotates about Godot -Z by <paramref name="vaneAngle"/>; the pin A (vane-pivot space) therefore sits at
    /// rel = P + Rot(-Z, th) A - B relative to the cylinder base B. The cylinder rotates about -Z by psi = atan2(-rel.y, rel.x) and the rod
    /// extends by |rel_xy| - L0 along the cylinder's local +X, where rel_xy is the IN-PLANE (x, y) length and L0 its value at th = 0 (verified against the kit bake to 0.35 mm; a 3D length is 0.11 m off). All inputs are in module space.
    /// </summary>
    public static void Ram(double vaneAngle, Vector3 vanePivot, Vector3 pin, Vector3 baseB, double l0, out double psi, out double rodX)
    {
        double c = Math.Cos(vaneAngle), s = Math.Sin(vaneAngle);
        double rx = vanePivot.X + pin.X * c + pin.Y * s - baseB.X;
        double ry = vanePivot.Y - pin.X * s + pin.Y * c - baseB.Y;
        psi = Math.Atan2(-ry, rx);
        rodX = Math.Sqrt(rx * rx + ry * ry) - l0;
    }
}
