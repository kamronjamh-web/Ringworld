using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// CPU double-precision port of the physical haze model (materials/ring_haze_shared.gdshaderinc, P1) used to derive the haze ambient and
/// the cloud ambient from the sky itself (docs/atmosphere-haze-revamp-plan.md P3) and as the reference for the sky parity test.
/// The port is line for line: ring_atmosphere_path_interval_shared, ring_haze_air_interval / _height / _flat_layer / _layer_integrals /
/// _optics / _sun_transmittance, both phase functions, and ring_haze_eval up to T, S_sun and the ambient weight. Parameters are read from the
/// RingAtmosphereModel statics, so the result tracks the same art controls the shaders receive. Camera at the origin, coordinate scale 1.
/// GDScript loads it with load("res://scripts/Atmosphere/RingHazeSkyIrradiance.cs").new() (same pattern as HazeConsistencyProbeHelper).
/// </summary>
public partial class RingHazeSkyIrradiance : RefCounted
{
    private const double SagFraction = 0.03;
    private const int AzimuthCount = 8, ElevationCount = 6;

    private readonly struct D3
    {
        public readonly double X, Y, Z;
        public D3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public D3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
        public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static D3 operator *(D3 a, D3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static D3 operator /(D3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
        public double Dot(D3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public double Length => Math.Sqrt(Dot(this));
        public D3 Normalized() { double l = Length; return l > 0.0 ? this / l : this; }
        public D3 Cross(D3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    }

    private struct Params
    {
        public D3 SigmaR;
        public double HR, SigmaA, OmegaA, HA, SigmaM0, OmegaM, HMist, CapHeight, G;
        public int Quality;
        public double Radius, FloorDepth;
    }

    private struct Frame
    {
        public D3 Outward, Axis;
        public double CameraHeight, SideNegative, SidePositive;
    }

    private static Params ModelParams(double radius, int qualityOverride)
    {
        Params p = default;
        p.SigmaR = new D3(RingAtmosphereModel.HazeSigmaRayleigh);
        p.HR = RingAtmosphereModel.HazeRayleighHeightMeters;
        // Storm haze (P9) goes through the float ApplyStorm mirror before promotion to double, so the CPU ambient follows the shaders.
        float sigmaA = RingAtmosphereModel.HazeSigmaAerosol, omegaA = RingAtmosphereModel.HazeOmegaAerosol, mieG = RingAtmosphereModel.HazeG;
        RingAtmosphereModel.ApplyStorm(RingAtmosphereModel.HazeStormVector, ref sigmaA, ref omegaA, ref mieG, RingAtmosphereModel.HazeSigmaMist, RingAtmosphereModel.HazeOmegaMist);
        p.SigmaA = sigmaA;
        p.OmegaA = omegaA;
        p.HA = RingAtmosphereModel.HazeAerosolHeightMeters;
        p.SigmaM0 = RingAtmosphereModel.HazeSigmaMist;
        p.OmegaM = RingAtmosphereModel.HazeOmegaMist;
        p.HMist = RingAtmosphereModel.HazeMistHeightMeters;
        p.CapHeight = RingAtmosphereModel.HazeCapHeightMeters;
        p.G = mieG;
        p.Quality = qualityOverride > 0 ? qualityOverride : RingAtmosphereModel.HazeQuality;
        p.Radius = radius;
        p.FloorDepth = RingAtmosphereModel.FloorDepthMeters;
        return p;
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private static double Smoothstep(double e0, double e1, double x)
    {
        double t = Clamp((x - e0) / (e1 - e0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    // ring_atmosphere_path_interval_shared at coordinate scale 1 with the camera at the origin. Returns (x, y), empty when y <= x.
    private static (double, double) PathInterval(D3 ray, Frame f, double radius, double layerHeight, double maxLength)
    {
        D3 axis = f.Axis.Normalized(), outward = f.Outward.Normalized();
        D3 radialRay = ray - axis * ray.Dot(axis);
        double a = radialRay.Dot(radialRay);
        double h = f.CameraHeight;
        double b = 2.0 * (radius - h) * outward.Dot(ray);
        double c = (layerHeight - h) * (2.0 * radius - h - layerHeight);
        double r0, r1;
        if (a <= 1e-12)
        {
            if (c <= 0.0) { r0 = 0.0; r1 = maxLength; } else return (1.0, 0.0);
        }
        else
        {
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return (1.0, 0.0);
            double q = -0.5 * (b + (b >= 0.0 ? 1.0 : -1.0) * Math.Sqrt(disc));
            double t0 = Math.Abs(q) > 1e-6 ? q / a : -b / (2.0 * a);
            double t1 = Math.Abs(q) > 1e-6 ? c / q : t0;
            r0 = Math.Min(t0, t1);
            r1 = Math.Max(t0, t1);
        }
        double across = ray.Dot(axis);
        if (Math.Abs(across) > 1e-6)
        {
            double wall0 = -f.SideNegative / across, wall1 = f.SidePositive / across;
            r0 = Math.Max(r0, Math.Min(wall0, wall1));
            r1 = Math.Min(r1, Math.Max(wall0, wall1));
        }
        else if (f.SideNegative < 0.0 || f.SidePositive < 0.0) return (1.0, 0.0);
        return (Math.Max(r0, 0.0), Math.Min(r1, maxLength));
    }

    // ring_haze_air_interval
    private static (double, double) AirInterval(D3 ray, double length, Frame f, Params p)
    {
        if (length > 0.0001 && length < Math.Min(Math.Min(p.CapHeight - f.CameraHeight, f.CameraHeight + p.FloorDepth), Math.Min(f.SideNegative, f.SidePositive))) return (0.0, length);
        var outer = PathInterval(ray, f, p.Radius, -p.FloorDepth, length);
        var vac = PathInterval(ray, f, p.Radius, p.CapHeight, length);
        if (outer.Item2 <= outer.Item1) return (1.0, 0.0);
        var air = outer;
        if (vac.Item2 > vac.Item1)
        {
            if (vac.Item1 <= air.Item1) air.Item1 = Math.Max(air.Item1, vac.Item2);
            else air.Item2 = Math.Min(air.Item2, vac.Item1);
        }
        return air;
    }

    private static double Height(double t, double h0, double radial0, double b, double a)
    {
        double n = b * t + a * t * t;
        double root = Math.Sqrt(Math.Max(radial0 * radial0 + n, 0.0));
        return h0 - n / Math.Max(radial0 + root, 0.001);
    }

    private static double FlatLayer(double hbar, double mu, double dt, double scaleHeight)
    {
        double H = Math.Max(scaleHeight, 1.0);
        double x = Math.Abs(mu) * dt / (2.0 * H);
        if (x < 0.001) return dt * Math.Exp(Math.Min(-hbar / H, 80.0)) * (1.0 + x * x * 0.1666667);
        return dt * Math.Exp(Math.Min(-hbar / H + x, 80.0)) * (1.0 - Math.Exp(-2.0 * x)) / (2.0 * x);
    }

    private static D3 LayerIntegrals(Params p, double h0, double radial0, double b, double a, double t0, double t1)
    {
        double dt = t1 - t0;
        if (dt <= 0.0) return new D3(0, 0, 0);
        int n = p.Quality >= 16 ? 16 : (p.Quality >= 8 ? 8 : 4);
        double sag = Math.Max(a, 0.0) * dt * dt / (8.0 * Math.Max(radial0, 1.0));
        double sagTol = Clamp(SagFraction * p.HMist, 4.0, 40.0);
        int pieces = sag <= sagTol ? 1 : Math.Min(n, Math.Max(2, (int)Math.Ceiling(1.5707963 * Math.Sqrt(sag / sagTol))));
        D3 sum = new(0, 0, 0);
        double tStart = t0;
        double hStart = Height(t0, h0, radial0, b, a);
        for (int i = 0; i < 16; i++)
        {
            if (i >= pieces) break;
            double tEnd = i + 1 == pieces ? t1 : t0 + dt * (0.5 - 0.5 * Math.Cos(3.14159265 * (i + 1) / pieces));
            double pieceDt = tEnd - tStart;
            double hMid = Height(0.5 * (tStart + tEnd), h0, radial0, b, a);
            double hEnd = Height(tEnd, h0, radial0, b, a);
            double hbar = (hStart + 4.0 * hMid + hEnd) / 6.0;
            double mu = (hEnd - hStart) / Math.Max(pieceDt, 0.000001);
            sum += new D3(FlatLayer(hbar, mu, pieceDt, p.HR), FlatLayer(hbar, mu, pieceDt, p.HA), FlatLayer(hbar, mu, pieceDt, p.HMist));
            hStart = hEnd;
            tStart = tEnd;
        }
        return sum;
    }

    private static D3 SunTransmittanceAt(Params p, double hMid, double cosSun)
    {
        double cap = p.CapHeight;
        double colR = Math.Max(p.HR * (Math.Exp(Math.Min(-hMid / Math.Max(p.HR, 1.0), 80.0)) - Math.Exp(-cap / Math.Max(p.HR, 1.0))), 0.0);
        double colA = Math.Max(p.HA * (Math.Exp(Math.Min(-hMid / Math.Max(p.HA, 1.0), 80.0)) - Math.Exp(-cap / Math.Max(p.HA, 1.0))), 0.0);
        double colM = Math.Max(p.HMist * (Math.Exp(Math.Min(-hMid / Math.Max(p.HMist, 1.0), 80.0)) - Math.Exp(-cap / Math.Max(p.HMist, 1.0))), 0.0);
        double mie = p.SigmaA * colA + p.SigmaM0 * colM;
        D3 tauCol = new(p.SigmaR.X * colR + mie, p.SigmaR.Y * colR + mie, p.SigmaR.Z * colR + mie);
        double airmass = 1.025 / (Math.Max(cosSun, 0.0) + 0.025);
        double vis = Smoothstep(-0.06, 0.0, cosSun);
        return new D3(Math.Exp(-tauCol.X * airmass), Math.Exp(-tauCol.Y * airmass), Math.Exp(-tauCol.Z * airmass)) * vis;
    }

    private static double PhaseRayleigh(double mu) => 0.0596831037 * (1.0 + mu * mu);

    private static double PhaseMie(double mu, double gIn)
    {
        double g = Clamp(gIn, -0.95, 0.95), g2 = g * g;
        return 0.1193662074 * (1.0 - g2) * (1.0 + mu * mu) / ((2.0 + g2) * Math.Pow(Math.Max(1.0 + g2 - 2.0 * g * mu, 0.0001), 1.5));
    }

    private static double OneMinusExp(double x) => x < 0.01 ? x * (1.0 - 0.5 * x * (1.0 - x * 0.3333333)) : 1.0 - Math.Exp(-x);

    private struct Eval
    {
        public D3 T, SSun, WAmb;
    }

    // ring_haze_eval for the sky (infinite ray to the shell exit) with E_sun = e, day = 1, lighting direction toLight.
    private static Eval EvalRay(Params p, Frame f, D3 dir, D3 toLight, D3 e)
    {
        Eval r = new() { T = new D3(1, 1, 1), SSun = new D3(0, 0, 0), WAmb = new D3(0, 0, 0) };
        D3 ray = dir.Normalized();
        double length = 2.0 * p.Radius;
        var interval = AirInterval(ray, length, f, p);
        if (interval.Item2 <= interval.Item1) return r;
        D3 axis = f.Axis.Normalized(), outward = f.Outward.Normalized();
        D3 radialRay = ray - axis * ray.Dot(axis);
        double radial0 = p.Radius - f.CameraHeight;
        double b = 2.0 * radial0 * outward.Dot(ray);
        double a = radialRay.Dot(radialRay);
        D3 layers = LayerIntegrals(p, f.CameraHeight, radial0, b, a, interval.Item1, interval.Item2);
        D3 tauR = p.SigmaR * layers.X;
        double tauA = p.SigmaA * layers.Y, tauM = p.SigmaM0 * layers.Z;
        double mieExt = tauA + tauM;
        D3 tau = new(tauR.X + mieExt, tauR.Y + mieExt, tauR.Z + mieExt);
        r.T = new D3(Math.Exp(-tau.X), Math.Exp(-tau.Y), Math.Exp(-tau.Z));
        D3 oneMinusT = new(OneMinusExp(tau.X), OneMinusExp(tau.Y), OneMinusExp(tau.Z));
        D3 inv = new(1.0 / Math.Max(tau.X, 1e-8), 1.0 / Math.Max(tau.Y, 1e-8), 1.0 / Math.Max(tau.Z, 1e-8));
        double mieScatter = p.OmegaA * tauA + p.OmegaM * tauM;
        D3 light = toLight.Normalized();
        double mu = ray.Dot(light);
        double pr = PhaseRayleigh(mu), pm = PhaseMie(mu, p.G);
        D3 weightSun = new(tauR.X * pr + mieScatter * pm, tauR.Y * pr + mieScatter * pm, tauR.Z * pr + mieScatter * pm);
        D3 weightAmb = new(tauR.X + mieScatter, tauR.Y + mieScatter, tauR.Z + mieScatter);
        // ring_haze_sun_transmittance at the path midpoint.
        double tMid = 0.5 * (interval.Item1 + interval.Item2);
        D3 radialMid = outward * radial0 + radialRay * tMid;
        double radialLen = Math.Max(radialMid.Length, 0.001);
        double cosSun = light.Dot(-radialMid / radialLen);
        D3 tSun = SunTransmittanceAt(p, p.Radius - radialLen, cosSun);
        r.SSun = oneMinusT * e * tSun * weightSun * inv;
        r.WAmb = oneMinusT * weightAmb * inv;
        return r;
    }

    private static Frame MakeFrame(Vector3 outward, Vector3 axis, double cameraHeight, double sideNeg, double sidePos)
        => new() { Outward = new D3(outward), Axis = new D3(axis), CameraHeight = cameraHeight, SideNegative = sideNeg, SidePositive = sidePos };

    /// <summary>
    /// Sky radiance reference: the shader's ring_haze_eval_sky with day = 1 and E_sun = e, returned as [T, S_sun, S_amb] where
    /// S_amb uses the given ambient (full day). quality 0 uses the model's quality.
    /// </summary>
    public Vector3[] EvalSky(Vector3 dir, Vector3 outward, Vector3 axis, float cameraHeight, float sideNegative, float sidePositive,
        float radius, Vector3 toSun, Vector3 e, Vector3 ambient, int quality)
    {
        Params p = ModelParams(radius, quality);
        Frame f = MakeFrame(outward, axis, cameraHeight, sideNegative, sidePositive);
        Eval r = EvalRay(p, f, new D3(dir), new D3(toSun), new D3(e));
        D3 sAmb = r.WAmb * new D3(ambient);
        return new[] { r.T.ToVector3(), r.SSun.ToVector3(), sAmb.ToVector3() };
    }

    /// <summary>Revision of the RingAtmosphereModel parameters (changes whenever the art controls or model switch).</summary>
    public long GetRevision() => (long)RingAtmosphereModel.Revision;

    /// <summary>Sets the P9 storm strength (RingAtmosphereModel owns the vector and the shader global).</summary>
    public void SetStorm(float strength) => RingAtmosphereModel.SetHazeStorm(strength);

    /// <summary>Bumps when the published storm vector changes; cached CPU ambient results key on it besides <see cref="GetRevision"/>.</summary>
    public long GetStormRevision() => (long)RingAtmosphereModel.StormRevision;

    /// <summary>The art sun-warmth tint the shaders multiply into the published sun irradiance (ring_haze_sun_tint).</summary>
    public Vector3 GetSunTint() => RingAtmosphereModel.HazeSunTint;

    public int GetQuality() => RingAtmosphereModel.HazeQuality;

    public bool IsPhysical() => RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical;

    /// <summary>
    /// Isotropic sky irradiance of the haze for a light from <paramref name="toLight"/> with irradiance <paramref name="e"/>.
    /// Averages the upper hemisphere (48 directions: sin(elevation) = (i + 0.5) / 6, 8 azimuths, equal weights, day = 1) for
    /// Sbar = mean S_sun and Wbar = mean (1 - T) w_amb / tau, adds the ground bounce
    /// E_g = E T_sun(camera) max(cos, 0) sun_vis, and sums the isotropic multi-scatter series in closed form:
    ///   A = gain [0.5 (1 + rho) Sbar + 0.5 rho E_g / pi] / max(1 - 0.5 (1 + rho) Wbar, 0.2)   (per channel)
    ///   Lup = Sbar + A Wbar  (mean upper-hemisphere radiance, used for the cloud ambient).
    /// Returns [Sbar, Wbar, Lup, A]; A is the ambient radiance colour for ringworld_haze_ambient_*.
    /// </summary>
    public Vector3[] Compute(float radius, Vector3 outward, Vector3 axis, float cameraHeight, float sideNegative, float sidePositive,
        Vector3 toLight, Vector3 e, float groundAlbedo, float multiscatterGain, int quality)
    {
        Params p = ModelParams(radius, quality);
        Frame f = MakeFrame(outward, axis, cameraHeight, sideNegative, sidePositive);
        D3 up = -f.Outward.Normalized();
        D3 ax = f.Axis.Normalized();
        ax = (ax - up * ax.Dot(up)).Normalized();
        D3 tang = up.Cross(ax);
        D3 light = new D3(toLight).Normalized();
        D3 eVec = new(e);
        D3 sSum = new(0, 0, 0), wSum = new(0, 0, 0);
        for (int i = 0; i < ElevationCount; i++)
        {
            double sinE = (i + 0.5) / ElevationCount;
            double cosE = Math.Sqrt(1.0 - sinE * sinE);
            for (int k = 0; k < AzimuthCount; k++)
            {
                double az = 2.0 * Math.PI * (k + 0.5) / AzimuthCount;
                D3 dir = up * sinE + (ax * Math.Cos(az) + tang * Math.Sin(az)) * cosE;
                Eval r = EvalRay(p, f, dir, light, eVec);
                sSum += r.SSun;
                wSum += r.WAmb;
            }
        }
        double count = ElevationCount * AzimuthCount;
        D3 sBar = sSum / count, wBar = wSum / count;
        double cosL = light.Dot(up);
        D3 eg = eVec * SunTransmittanceAt(p, cameraHeight, cosL) * Math.Max(cosL, 0.0);
        double rho = groundAlbedo;
        D3 numerator = (sBar * (0.5 * (1.0 + rho)) + eg * (0.5 * rho / Math.PI)) * multiscatterGain;
        D3 denominator = new(
            Math.Max(1.0 - 0.5 * (1.0 + rho) * wBar.X, 0.2), Math.Max(1.0 - 0.5 * (1.0 + rho) * wBar.Y, 0.2), Math.Max(1.0 - 0.5 * (1.0 + rho) * wBar.Z, 0.2));
        D3 a = new(numerator.X / denominator.X, numerator.Y / denominator.Y, numerator.Z / denominator.Z);
        D3 lup = sBar + a * wBar;
        return new[] { sBar.ToVector3(), wBar.ToVector3(), lup.ToVector3(), a.ToVector3() };
    }
}
