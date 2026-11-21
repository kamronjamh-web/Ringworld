using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T7-1P: CPU PROTOTYPE of the planned T7 lowland meso-relief terms (docs/work-orders/terrain-vegetation/T7.md P1-P10) on top of the real seed-1337 rivers review world.
/// Pictures and numbers only; not production code. Every parameter is a CLI override (--name=value after the "--"). Output: .godot/audit_baseline/common/t7/prototype/&lt;label&gt;/.
/// </summary>
public partial class T7MesoPrototype : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;
    const double WorldWidth = EmitterSiteTestWorlds.Width;
    const int Seed = 1337;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- parameters ----------------------------------------------------------------------------------------------------------------------------------

    sealed class Cfg
    {
        public double style_scale = 48000, hummock_threshold = 0.58;
        public double ripple_rms = 0.12, ripple_scale = 12, swell_rms = 1.8, swell_scale = 110, swell_hummock_cut = 0.6;
        public double hummock_spacing = 180, knoll_prob = 0.55, hollow_prob = 0.35, hummock_height = 12, hollow_depth_ratio = 0.8;
        public double cliff_threshold = 0.55, cliff_height = 28, cliff_face_ratio = 0.7, cliff_top_m = 400, cliff_back_m = 1200;
        public double cove_cell = 6000, cove_freq = 0.5, cove_wmin = 200, cove_wmax = 3000, cove_depth = 10, cove_cam_dist = 2500;
        public double ripple_octaves = 2, swell_octaves = 2, cove_outline = 0.9, cliff_retreat_m = 120;
        public double contour = 0, contour_hummock = 2, contour_coast = 10, plan_exag = 1;
        public Cfg Clone() => (Cfg)MemberwiseClone();
    }

    readonly record struct Terms(bool Base, bool Hummock, bool Cliff, bool Cove, bool ForceHum, bool Retreat = false)
    {
        public static readonly Terms None = new(false, false, false, false, false);
        public Terms WithForce(bool f) => this with { ForceHum = f };
    }

    sealed class Scene
    {
        public Cfg C = new();
        public Terms T;
        public CoveField Coves = new();
        public ConcurrentDictionary<(int, int), Pond?> Ponds = new();
    }

    // ---- math helpers ----------------------------------------------------------------------------------------------------------------------------------

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    static double S(double a, double b, double x) { double t = Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    static double U(double v) => Clamp(0.5 * v + 0.5, 0, 1);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Elig(TerrainGenerationHeightModel.Diagnostics d) => 1 - S(0.02, 0.2, d.AlpineWeight);

    static ulong Mix(ulong x) { x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33; return x; }
    static double Hash01(int i, int k, int seed, int axis)
    {
        ulong x = (ulong)(uint)i * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)k * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)seed * 0x165667B19E3779F9UL ^ (ulong)(axis + 1) * 0x27D4EB2F165667C5UL;
        return (Mix(x) >> 11) * (1.0 / (1UL << 53));
    }

    // ---- noise ------------------------------------------------------------------------------------------------------------------------------------------

    static readonly (string Name, int Offset, string ScaleField, double FixedScale, string OctField)[] NoiseDefs =
    {
        ("style", 1601, "style_scale", 0, ""), ("ripple", 1607, "ripple_scale", 0, "ripple_octaves"), ("swell", 1609, "swell_scale", 0, "swell_octaves"),
        ("cliff_mask", 1613, "", 24000, ""), ("cliff_h1", 1619, "", 6000, ""), ("cliff_h2", 1621, "", 200, ""), ("cliff_ret", 1623, "", 500, ""),
    };

    sealed class Nz
    {
        readonly FastNoiseLite _n = new();
        public double Scale = 1;
        public Nz(int offset, double scale, int octaves = 2)
        {
            _n.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
            _n.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
            _n.FractalOctaves = octaves;
            _n.Frequency = (float)(1d / scale);
            _n.Seed = Seed + offset;
        }
        public double At(double a, double z) => _n.GetNoise2D((float)a, (float)z) * Scale;
    }

    static int NoiseOct(Cfg c, (string Name, int Offset, string ScaleField, double FixedScale, string OctField) d)
        => d.OctField.Length == 0 ? 2 : (int)(double)typeof(Cfg).GetField(d.OctField)!.GetValue(c)!;

    static double NoiseScaleOf(Cfg c, (string Name, int Offset, string ScaleField, double FixedScale, string OctField) d)
        => d.ScaleField.Length == 0 ? d.FixedScale : (double)typeof(Cfg).GetField(d.ScaleField)!.GetValue(c)!;

    // ---- per-worker context -----------------------------------------------------------------------------------------------------------------------------

    readonly record struct Smp(float H, float WL, byte K); // K: 0 land, 1 ocean, 2 river/lake, 3 pond

    sealed class Pond { public double Level, Ca, Cz, R, Ratio, Ori; public int I, K; }

    sealed class Cove
    {
        public int I, K;
        public double Oa, Oz, Nx, Nz, Wcl; // natural shoreline point, unit inland normal
        public double[] H = new double[10];
        public double Rw, Ca, Cz, D, Wb, TanAlpha, RhoMax, Ro;
        public bool Forced;
        public void Build(Cfg c, double rw)
        {
            Rw = rw;
            double inset = 0.35 + 0.5 * H[3];
            Ca = Oa + Nx * inset * Rw; Cz = Oz + Nz * inset * Rw;
            D = c.cove_depth * Clamp(Math.Sqrt(Rw / 750d), 0.4, 1.5);
            Wb = (30 + 50 * H[8]) * (1 - 0.7 * Wcl);
            double alpha = (50 + 20 * H[9]) * Math.PI / 180d;
            TanAlpha = Math.Tan(alpha);
            Outline = c.cove_outline;
            RhoMax = Rw * (1 + Outline * (0.5 + 1d / 3 + 0.25 + 0.2 + 1d / 6 + 1d / 7));
            Ro = RhoMax + Wb + 200d / TanAlpha;
        }
        public double[] Am = new double[8], Ph = new double[8]; public double Outline = 0.9;
        public double Rho(double th) { double s = 1; for (int q = 2; q <= 7; q++) s += Outline * Am[q] / q * Math.Cos(q * th + 2 * Math.PI * Ph[q]); return Rw * Math.Max(0.35, s); }
        public (double Min, double Max) RhoRange() { double mn = 9, mx = 0; for (int n = 0; n < 720; n++) { double r = Rho(n * Math.PI / 360) / Rw; mn = Math.Min(mn, r); mx = Math.Max(mx, r); } return (mn, mx); }
        public Cove Clone() { var c = (Cove)MemberwiseClone(); c.H = (double[])H.Clone(); return c; }
    }

    sealed class CoveField
    {
        public ConcurrentDictionary<(int, int), Cove?> Cache = new();
        public ConcurrentDictionary<string, int> Counts = new();
        public Dictionary<(int, int), Cove>? Overrides;
        public CoveField Derive(Dictionary<(int, int), Cove> overrides) => new() { Cache = Cache, Counts = Counts, Overrides = overrides };
        public Cove? Get(Ctx cx, Cfg c, int i, int k)
        {
            if (Overrides != null && Overrides.TryGetValue((i, k), out Cove? o)) return o;
            if (Cache.TryGetValue((i, k), out Cove? v)) return v;
            Cove? r = cx.MakeCove(c, i, k, out string cause);
            if (Cache.TryAdd((i, k), r)) Counts.AddOrUpdate(cause, 1, (_, n) => n + 1);
            return r;
        }
    }

    sealed class Ctx
    {
        public readonly TerrainGenerationHeightModel M;
        public readonly RiverCarveOperator? Op;
        public readonly RiverGraph? G;
        public readonly Nz Style, Ripple, Swell, CliffMask, CliffH1, CliffH2, CliffRet;
        const int HumSeed = Seed + 1631, CoveSeed = Seed + 1700;

        public Ctx(TerrainGenerationParameters recipe, Cfg c, double[] rms)
        {
            M = new TerrainGenerationHeightModel(recipe);
            Op = M.RiverOperator; G = Op?.Graph;
            Nz[] n = new Nz[NoiseDefs.Length];
            for (int i = 0; i < n.Length; i++) { n[i] = new Nz(NoiseDefs[i].Offset, NoiseScaleOf(c, NoiseDefs[i]), NoiseOct(c, NoiseDefs[i])); n[i].Scale = 1d / rms[i]; }
            Style = n[0]; Ripple = n[1]; Swell = n[2]; CliffMask = n[3]; CliffH1 = n[4]; CliffH2 = n[5]; CliffRet = n[6];
        }

        // -- shore distance (term 1) --
        public double ShoreDist(double a, double z, double c)
        {
            if (c < 0 || c > 0.06) return double.PositiveInfinity;
            const double st = 6000d;
            double gx = (M.SampleNaturalDiagnostics(a + st, z).Continentalness - M.SampleNaturalDiagnostics(a - st, z).Continentalness) / (2 * st);
            double gz = (M.SampleNaturalDiagnostics(a, z + st).Continentalness - M.SampleNaturalDiagnostics(a, z - st).Continentalness) / (2 * st);
            double g = Math.Max(Math.Sqrt(gx * gx + gz * gz), 1d / (32d * 300000d));
            return c / g;
        }

        public void Grad(double a, double z, out double c, out double gx, out double gz)
        {
            const double st = 6000d;
            c = M.SampleNaturalDiagnostics(a, z).Continentalness;
            gx = (M.SampleNaturalDiagnostics(a + st, z).Continentalness - M.SampleNaturalDiagnostics(a - st, z).Continentalness) / (2 * st);
            gz = (M.SampleNaturalDiagnostics(a, z + st).Continentalness - M.SampleNaturalDiagnostics(a, z - st).Continentalness) / (2 * st);
        }

        public double WhAt(Cfg c, double a, double z, double ruggedness, double ds)
            => S(c.hummock_threshold, c.hummock_threshold + 0.08, U(Style.At(a, z))) * (1 - S(0.35, 0.7, ruggedness)) * S(150, 400, ds);
        public double WclAt(Cfg c, double a, double z) => S(c.cliff_threshold, c.cliff_threshold + 0.06, U(CliffMask.At(a, z)));
        double HclAt(Cfg c, double a, double z) => c.cliff_height * (0.6 + 0.4 * U(CliffH1.At(a, z))) * (0.75 + 0.25 * U(CliffH2.At(a, z)));

        // -- hummock lattice (term 5) --
        public static void HumCell(Cfg c, int i, int k, out int type, out double ca, out double cz, out double R, out double ratio, out double ori, out double amp)
        {
            double L = c.hummock_spacing;
            double h1 = Hash01(i, k, HumSeed, 2), h2 = Hash01(i, k, HumSeed, 3), h3 = Hash01(i, k, HumSeed, 4), h4 = Hash01(i, k, HumSeed, 5), h5 = Hash01(i, k, HumSeed, 6);
            type = h1 < c.knoll_prob ? 1 : h1 > 1 - c.hollow_prob ? 2 : 0;
            ca = (i + 0.3 + 0.4 * Hash01(i, k, HumSeed, 0)) * L; cz = (k + 0.3 + 0.4 * Hash01(i, k, HumSeed, 1)) * L;
            R = L * (0.45 + 0.25 * h2); ratio = 0.7 + 0.3 * h3; ori = Math.PI * h4;
            double H = c.hummock_height * (0.6 + 0.4 * h5);
            amp = type == 1 ? H : type == 2 ? -c.hollow_depth_ratio * H : 0;
        }

        public static double Q2(double da, double dz, double R, double ratio, double ori)
        {
            double co = Math.Cos(ori), si = Math.Sin(ori);
            double u = da * co + dz * si, v = -da * si + dz * co;
            return (u / R) * (u / R) + (v / (R * ratio)) * (v / (R * ratio));
        }

        double HumSum(Cfg c, double a, double z)
        {
            double L = c.hummock_spacing, sum = 0;
            int i0 = (int)Math.Floor(a / L), k0 = (int)Math.Floor(z / L);
            for (int i = i0 - 1; i <= i0 + 1; i++)
                for (int k = k0 - 1; k <= k0 + 1; k++)
                {
                    HumCell(c, i, k, out int type, out double ca, out double cz, out double R, out double ratio, out double ori, out double amp);
                    if (type == 0) continue;
                    double q2 = Q2(a - ca, z - cz, R, ratio, ori);
                    if (q2 >= 1) continue;
                    double kk = 1 - q2; sum += amp * kk * kk * kk;
                }
            return sum;
        }

        // -- the full evaluation --
        public Smp Eval(Scene s, double a, double z, bool ponds)
        {
            var d = M.SampleDiagnostics(a, z);
            double hc = d.Height, h = hc;
            Cfg c = s.C; Terms t = s.T;
            if (hc > 0 && (t.Base || t.Hummock || t.Cliff))
            {
                double fr = 1d - (Op?.CorridorWeight(a, z, 0) ?? 0d);
                bool siteFree = d.SiteWeight == 0d && d.WeatherLungWeight == 0d;
                double frs = siteFree ? fr : 0d;
                double F = frs * Elig(d);
                double ds = (F > 0 || (frs > 0 && t.Cliff)) ? ShoreDist(a, z, d.Continentalness) : double.PositiveInfinity;
                double fShore = S(0, 150, ds);
                double wh = 0;
                if (F > 0 && (t.Base || t.Hummock)) wh = t.ForceHum ? 1d : WhAt(c, a, z, d.Ruggedness, ds);
                double delta = 0;
                if (t.Base && F > 0)
                    delta += F * fShore * (c.ripple_rms * Ripple.At(a, z) + (1 - c.swell_hummock_cut * wh) * c.swell_rms * Swell.At(a, z));
                if (t.Hummock && F > 0 && wh > 0) delta += F * wh * HumSum(c, a, z);
                double ret = 0;
                if (t.Cliff && frs > 0 && !double.IsPositiveInfinity(ds))
                {
                    double wcl = WclAt(c, a, z);
                    if (wcl > 0)
                    {
                        if (t.Retreat && c.cliff_retreat_m > 0)
                        {
                            double fret = siteFree ? 1d : 0d;
                            if (Op != null)
                            {
                                Span<RiverChannelNear> buf = stackalloc RiverChannelNear[8];
                                int nc = Op.ChannelsNear(a, z, 450, buf);
                                double dm = double.MaxValue;
                                for (int q = 0; q < nc; q++) dm = Math.Min(dm, Math.Abs(buf[q].T));
                                if (nc > 0) fret *= S(300, 450, dm);
                            }
                            if (fret > 0) ret = c.cliff_retreat_m * wcl * Math.Pow(U(CliffRet.At(a, z)), 1.5) * fret;
                        }
                        double Hc = HclAt(c, a, z), Wf = c.cliff_face_ratio * Hc;
                        delta += frs * wcl * Hc * S(0, Wf, ds - ret) * (1 - S(c.cliff_top_m, c.cliff_top_m + c.cliff_back_m, ds));
                    }
                }
                h = Math.Max(hc + delta, Math.Min(hc, 0.5));
                if (ret > 0)
                {
                    double pr = ds < ret ? -2 * S(0, 20, ds) * S(0, 20, ret - ds) : 1.7320508 * (ds - ret);
                    if (pr < 250 && pr < h) h = pr;
                }
            }
            if (t.Cove) h = ApplyCoves(s, a, z, h);
            float wl = float.NegativeInfinity; byte kind = 0;
            if (d.InlandWater != InlandWaterKind.None && !double.IsNaN(d.WaterLevel) && h < d.WaterLevel) { wl = (float)d.WaterLevel; kind = 2; }
            else if (h < 0) { wl = 0; kind = 1; }
            if (ponds && kind == 0 && t.Hummock)
            {
                double L = c.hummock_spacing;
                int i0 = (int)Math.Floor(a / L), k0 = (int)Math.Floor(z / L);
                for (int i = i0 - 1; i <= i0 + 1 && kind == 0; i++)
                    for (int k = k0 - 1; k <= k0 + 1; k++)
                    {
                        HumCell(c, i, k, out int type, out double ca, out double cz, out double R, out double ratio, out double ori, out _);
                        if (type != 2 || Q2(a - ca, z - cz, R, ratio, ori) >= 1) continue;
                        Pond? p = GetPond(s, i, k);
                        if (p != null && h < p.Level) { wl = (float)p.Level; kind = 3; break; }
                    }
            }
            return new Smp((float)h, wl, kind);
        }

        public double Height(Scene s, double a, double z) => Eval(s, a, z, false).H;

        public Pond? GetPond(Scene s, int i, int k)
        {
            if (s.Ponds.TryGetValue((i, k), out Pond? cached)) return cached;
            HumCell(s.C, i, k, out int type, out double ca, out double cz, out double R, out double ratio, out double ori, out _);
            Pond? pond = null;
            if (type == 2)
            {
                double co = Math.Cos(ori), si = Math.Sin(ori), rim = double.MaxValue;
                for (int n = 0; n < 32; n++)
                {
                    double ph = 2 * Math.PI * n / 32, u = 1.05 * R * Math.Cos(ph), v = 1.05 * R * ratio * Math.Sin(ph);
                    rim = Math.Min(rim, Height(s, ca + u * co - v * si, cz + u * si + v * co));
                }
                double level = rim - 0.3, floor = Height(s, ca, cz);
                if (floor < level - 0.3) pond = new Pond { Level = level, Ca = ca, Cz = cz, R = R, Ratio = ratio, Ori = ori, I = i, K = k };
            }
            s.Ponds.TryAdd((i, k), pond);
            return pond;
        }

        // -- coves (term 7) --
        double ApplyCoves(Scene s, double a, double z, double h)
        {
            Cfg c = s.C; double cell = c.cove_cell;
            int ci = (int)Math.Floor(a / cell), ck = (int)Math.Floor(z / cell);
            for (int i = ci - 1; i <= ci + 1; i++)
                for (int k = ck - 1; k <= ck + 1; k++)
                {
                    Cove? cv = s.Coves.Get(this, c, i, k);
                    if (cv == null) continue;
                    double dx = a - cv.Ca, dz = z - cv.Cz, r2 = dx * dx + dz * dz;
                    if (r2 > cv.Ro * cv.Ro) continue;
                    double r = Math.Sqrt(r2), rho = cv.Rho(Math.Atan2(dz, dx)), e = r - rho, P;
                    if (e < 0) { double q = r / rho; P = -cv.D * (1 - q * q); }
                    else if (e < cv.Wb) P = 0.06 * e;
                    else P = 0.06 * cv.Wb + cv.TanAlpha * (e - cv.Wb);
                    if (P < h) h = P;
                }
            return h;
        }

        public Cove? MakeCove(Cfg c, int i, int k, out string cause)
        {
            double cell = c.cove_cell;
            double qa = (i + 0.3 + 0.4 * Hash01(i, k, CoveSeed, 0)) * cell, qz = (k + 0.3 + 0.4 * Hash01(i, k, CoveSeed, 1)) * cell;
            double pa = qa, pz = qz, cc = 0, gx = 0, gz = 0;
            for (int step = 0; step < 3; step++)
            {
                Grad(pa, pz, out cc, out gx, out gz);
                if (Math.Abs(cc) > 0.06) { cause = "no_shore_band"; return null; }
                double g2 = gx * gx + gz * gz;
                if (g2 < 1e-30) { cause = "grad_zero"; return null; }
                if (step < 2) { pa -= cc / g2 * gx; pz -= cc / g2 * gz; }
            }
            double gl = Math.Sqrt(gx * gx + gz * gz);
            if (Math.Abs(cc / gl) >= 20) { cause = "not_on_shore"; return null; }
            if (Math.Sqrt((pa - qa) * (pa - qa) + (pz - qz) * (pz - qz)) > cell * 0.5) { cause = "proj_far"; return null; }
            var cv = new Cove { I = i, K = k, Oa = pa, Oz = pz, Nx = gx / gl, Nz = gz / gl };
            for (int n = 1; n <= 9; n++) cv.H[n] = Hash01(i, k, CoveSeed, 1 + n);
            for (int q = 2; q <= 7; q++) { cv.Am[q] = Hash01(i, k, CoveSeed, 20 + 2 * q); cv.Ph[q] = Hash01(i, k, CoveSeed, 21 + 2 * q); }
            cv.Wcl = WclAt(c, pa, pz);
            if (cv.H[1] >= c.cove_freq * (0.5 + 0.5 * cv.Wcl)) { cause = "freq"; return null; }
            double rw = 0.5 * c.cove_wmin * Math.Pow(c.cove_wmax / c.cove_wmin, cv.H[2]);
            cv.Build(c, rw);
            if (M.SampleNaturalDiagnostics(pa - cv.Nx * (rw + 300), pz - cv.Nz * (rw + 300)).Height >= -1d) { cause = "no_sea"; return null; }
            double rk = cv.Ro + 300;
            if (Op != null)
            {
                Span<RiverChannelNear> buf = stackalloc RiverChannelNear[2];
                if (Op.ChannelsNear(pa, pz, Math.Min(rk, 2300d), buf) > 0) { cause = "river_channel"; return null; }
            }
            for (double da = -rk; da <= rk; da += 100)
                for (double dz = -rk; dz <= rk; dz += 100)
                {
                    if (da * da + dz * dz > rk * rk) continue;
                    double xa = pa + da, xz = pz + dz;
                    if (G != null && (G.LakeAt(xa, xz) >= 0 || G.DryBasinAt(xa, xz) >= 0)) { cause = "lake_basin"; return null; }
                    var d = M.SampleDiagnostics(xa, xz);
                    if (d.SiteWeight > 0 || d.WeatherLungWeight > 0) { cause = "site"; return null; }
                }
            cause = "accepted";
            return cv;
        }
    }

    // ---- run state -----------------------------------------------------------------------------------------------------------------------------------

    TerrainGenerationParameters _recipe = null!;
    Cfg _cfg = new();
    double[] _rms = Array.Empty<double>();
    readonly ConcurrentBag<Ctx> _pool = new();
    int _workers = 16;
    string _out = "";
    readonly StringBuilder _num = new();
    Terms _all;
    bool _ok = true;
    HashSet<string> _stages = new();
    bool St(string s) => _stages.Contains(s);

    Ctx Rent() => _pool.TryTake(out Ctx? c) ? c : new Ctx(_recipe, _cfg, _rms);
    void Return(Ctx c) => _pool.Add(c);
    void ParFor(int n, Action<Ctx, int> body)
        => Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = _workers }, () => Rent(), (i, _, cx) => { body(cx, i); return cx; }, Return);

    void Num(string line) { _num.AppendLine(line); Console.WriteLine("T7P " + line); }
    void Flush() => File.WriteAllText(Path.Combine(_out, "numbers.txt"), _num.ToString());

    static Dictionary<string, string> ParseArgs()
    {
        var d = new Dictionary<string, string>();
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--")) continue;
            string body = arg[2..]; int eq = body.IndexOf('=');
            string name = (eq < 0 ? body : body[..eq]).Replace('-', '_');
            d[name] = eq < 0 ? "true" : body[(eq + 1)..];
        }
        return d;
    }

    static Terms ParseTerms(string list)
    {
        var set = new HashSet<string>(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new Terms(set.Contains("base"), set.Contains("hummock"), set.Contains("cliff"), set.Contains("cove"), false, set.Contains("retreat"));
    }

    Scene MakeScene(Terms t, Cfg? cfg = null, CoveField? coves = null)
        => new() { C = cfg ?? _cfg, T = t, Coves = coves ?? _coveBase };
    CoveField _coveBase = new();

    // ---- entry ---------------------------------------------------------------------------------------------------------------------------------------

    public int Run()
    {
        var swAll = Stopwatch.StartNew();
        string work = ProjectSettings.GlobalizePath($"res://.godot/t7proto_{System.Environment.ProcessId}");
        try
        {
            var args = ParseArgs();
            string label = args.GetValueOrDefault("label", "run");
            _out = ProjectSettings.GlobalizePath($"res://.godot/audit_baseline/common/t7/prototype/{label}");
            Directory.CreateDirectory(_out);
            foreach (var f in typeof(Cfg).GetFields())
                if (args.TryGetValue(f.Name, out string? v)) f.SetValue(_cfg, double.Parse(v, Inv));
            string termsArg = args.GetValueOrDefault("terms", "base,hummock,cliff,cove,retreat");
            _all = ParseTerms(termsArg);
            bool forceHum = args.ContainsKey("force_hummock");
            _stages = new HashSet<string>(args.GetValueOrDefault("stages", "coast,plains,hummock,stats,style").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            _workers = args.TryGetValue("workers", out string? ws) ? int.Parse(ws) : Math.Min(16, System.Environment.ProcessorCount);
            string forced = args.GetValueOrDefault("cove_force_widths", "1000,2000,3000");
            double[] forcedWidths = forced.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, Inv)).ToArray();
            Num($"T7 meso prototype label={label} terms_after={termsArg} workers={_workers} force_hummock_flag={forceHum}");
            Num("parameters: " + string.Join(" ", typeof(Cfg).GetFields().Select(f => $"{f.Name}={((double)f.GetValue(_cfg)!).ToString("0.###", Inv)}")));

            // ---- world ----
            var sw = Stopwatch.StartNew();
            _recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
            using (VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, _recipe))
            {
                Directory.CreateDirectory(work);
                RiverGraphStore.GetOrBuild(HydrologyBakeInputs.Capture(settings, _recipe, HydrologyBakeInputs.DefaultWorkers, HydrologySourcePreference.Cpu, work));
            }
            Num($"stage world: river graph CPU bake {sw.Elapsed.TotalSeconds:F1} s");

            // ---- noise RMS ----
            sw.Restart();
            _rms = new double[NoiseDefs.Length];
            for (int n = 0; n < NoiseDefs.Length; n++)
            {
                var nz = new Nz(NoiseDefs[n].Offset, NoiseScaleOf(_cfg, NoiseDefs[n]), NoiseOct(_cfg, NoiseDefs[n]));
                double ss = 0;
                for (int i = 0; i < 100000; i++) { double v = nz.At(Hash01(i, 0, 91, n) * Circumference, Hash01(i, 1, 91, n) * WorldWidth); ss += v * v; }
                _rms[n] = Math.Sqrt(ss / 100000d);
            }
            Num("noise RMS constants (raw FastNoiseLite SimplexSmooth FBm, 2 oct except ripple/swell octaves per CLI, 100000 pts; each band divided by its value): " + string.Join(" ", NoiseDefs.Select((d, n) => $"{d.Name}={_rms[n]:F4}")));
            for (int i = 0; i < _workers; i++) _pool.Add(new Ctx(_recipe, _cfg, _rms));
            Num($"stage noise/ctx setup {sw.Elapsed.TotalSeconds:F1} s");

            // ---- style share ----
            sw.Restart();
            if (St("style")) StyleShare();
            Num($"stage style share {sw.Elapsed.TotalSeconds:F1} s");
            Flush();

            // ---- coast window ----
            sw.Restart();
            if (St("coast")) CoastStage(forcedWidths);
            Num($"stage coast {sw.Elapsed.TotalSeconds:F1} s");
            Flush();

            // ---- plains ----
            sw.Restart();
            var plainsEye = (1727863.125, 256500d);
            var before = MakeScene(Terms.None); var after = MakeScene(_all);
            if (St("plains")) foreach ((double h, double pitch, string tag) in new[] { (1.7, 0d, "h1.7"), (30d, -8d, "h30") })
            {
                RenderOblique(before, plainsEye.Item1, plainsEye.Item2, 0, h, pitch, $"plains_oblique_{tag}_before.png", 0);
                RenderOblique(after, plainsEye.Item1, plainsEye.Item2, 0, h, pitch, $"plains_oblique_{tag}_after.png", 0);
            }
            Num($"stage plains {sw.Elapsed.TotalSeconds:F1} s");
            Flush();

            // ---- hummock country ----
            sw.Restart();
            if (St("hummock") || St("style")) HummockStage(plainsEye, forceHum);
            Num($"stage hummock {sw.Elapsed.TotalSeconds:F1} s");
            Flush();

            // ---- statistics ----
            sw.Restart();
            if (St("stats")) PlainsStats(forceHum);
            Num($"stage plains stats {sw.Elapsed.TotalSeconds:F1} s");
            Num($"total {swAll.Elapsed.TotalMinutes:F1} min");
            Flush();
        }
        catch (Exception e)
        {
            _ok = false;
            GD.PrintErr("T7_MESO_PROTOTYPE exception " + e);
            Num("EXCEPTION " + e.Message);
            try { Flush(); } catch { }
        }
        finally
        {
            RiverGraphStore.Clear();
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
        return _ok ? 0 : 1;
    }

    // ---- style share + timing ------------------------------------------------------------------------------------------------------------------------

    void StyleShare()
    {
        int target = 20000, found = 0, tries = 0, hum = 0;
        var sc = MakeScene(_all);
        int[] counts = new int[2]; // [0] tries, [1] found+hum
        object gate = new();
        int batch = 4000, next = 0;
        var cnt = new int[3]; // tries, eligible, hum>=0.5
        while (cnt[1] < target && cnt[0] < 4_000_000)
        {
            int b0 = next; next += batch;
            ParFor(batch / 100, (cx, bi) =>
            {
                int et = 0, el = 0, hm = 0;
                for (int n = 0; n < 100; n++)
                {
                    int idx = b0 + bi * 100 + n;
                    double a = Hash01(idx, 0, 77, 0) * Circumference, z = 1000 + Hash01(idx, 1, 77, 1) * (WorldWidth - 2000);
                    et++;
                    var d = cx.M.SampleDiagnostics(a, z);
                    if (d.Height <= 0 || Elig(d) <= 0.5) continue;
                    el++;
                    double ds = cx.ShoreDist(a, z, d.Continentalness);
                    if (cx.WhAt(_cfg, a, z, d.Ruggedness, ds) >= 0.5) hm++;
                }
                lock (gate) { cnt[0] += et; cnt[1] += el; cnt[2] += hm; }
            });
        }
        found = cnt[1]; tries = cnt[0]; hum = cnt[2];
        Num($"hummock style share: w_h>=0.5 on {hum}/{found} = {100.0 * hum / Math.Max(1, found):F1} % of eligible lowland land points (E>0.5, h_c>0; hummock_threshold={_cfg.hummock_threshold}; {tries} random points drawn, {100.0 * found / Math.Max(1, tries):F1} % eligible)");
    }

    // ---- coast ----------------------------------------------------------------------------------------------------------------------------------------

    (double A, double Z) _coastCentre;
    List<Cove> _coastCoves = new();

    void CoastStage(double[] forcedWidths)
    {
        var eye = (235067.125, 262100d);
        double cell = _cfg.cove_cell;
        int i0 = (int)Math.Floor((eye.Item1 - 150000) / cell), i1 = (int)Math.Floor((eye.Item1 + 150000) / cell);
        int k0 = (int)Math.Floor(20000 / cell), k1 = (int)Math.Floor(298000 / cell);
        var cells = new List<(int, int)>();
        for (int i = Math.Max(i0, 0); i <= i1; i++) for (int k = k0; k <= k1; k++) cells.Add((i, k));
        var sw = Stopwatch.StartNew();
        var field = _coveBase;
        ParFor(cells.Count, (cx, n) => field.Get(cx, _cfg, cells[n].Item1, cells[n].Item2));
        var accepted = cells.Select(c => field.Cache.TryGetValue(c, out Cove? v) ? v : null).Where(v => v != null).Select(v => v!).ToList();
        Num($"coves: {cells.Count} cells evaluated in {sw.Elapsed.TotalSeconds:F1} s over around {eye.Item1 - 150000:F0}..{eye.Item1 + 150000:F0}, across 20000..298000: accepted {accepted.Count}");
        Num("cove cell outcomes (natural coves, whole search region): " + string.Join(", ", field.Counts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")));

        // Candidate 16 km windows ordered by distance from the coast_eye pose.
        double half = 8000;
        var cands = new List<(double D, double A, double Z, int N)>();
        for (double ca = Math.Max(half, eye.Item1 - 150000); ca <= eye.Item1 + 150000; ca += 2000)
            for (double cz = 20000 + half; cz <= 298000 - half; cz += 2000)
            {
                int cnt = 0;
                foreach (Cove v in accepted) if (Math.Abs(v.Oa - ca) < half && Math.Abs(v.Oz - cz) < half) cnt++;
                if (cnt >= 3) cands.Add((Math.Sqrt((ca - eye.Item1) * (ca - eye.Item1) + (cz - eye.Item2) * (cz - eye.Item2)), ca, cz, cnt));
            }
        cands.Sort((x, y) => x.D.CompareTo(y.D));
        Num($"coast window candidates with >=3 accepted coves: {cands.Count}");
        (double D, double A, double Z, int N)? best = null;
        var probe = Rent();
        foreach (var cd in cands)
        {
            double mx = 0;
            // Cliff weight > 0.5 must occur on the shore itself (within 300 m of the shoreline by c/|grad c|), where cliffs can stand.
            for (double a = cd.A - half; a <= cd.A + half && mx <= 0.5; a += 250) for (double z = cd.Z - half; z <= cd.Z + half && mx <= 0.5; z += 250)
                {
                    double w = probe.WclAt(_cfg, a, z);
                    if (w <= 0.5) continue;
                    double cc = probe.M.SampleNaturalDiagnostics(a, z).Continentalness;
                    if (cc < 0 || cc > 0.06) continue;
                    probe.Grad(a, z, out _, out double gx, out double gz);
                    if (cc / Math.Sqrt(gx * gx + gz * gz) <= 300) mx = w;
                }
            if (mx > 0.5) { best = cd; break; }
        }
        Return(probe);
        if (best == null) { _ok = false; Num("ESCALATE: no 16 km window with >=3 accepted coves and cliff weight > 0.5"); return; }
        var win = best.Value;
        _coastCentre = (win.A, win.Z);
        Num($"COAST WINDOW centre around={win.A:F0} across={win.Z:F0} (16 x 16 km), {win.N} accepted coves, {win.D / 1000:F1} km from coast_eye");
        _coastCoves = accepted.Where(v => Math.Abs(v.Oa - win.A) < half && Math.Abs(v.Oz - win.Z) < half)
            .OrderBy(v => (v.Oa - win.A) * (v.Oa - win.A) + (v.Oz - win.Z) * (v.Oz - win.Z)).ToList();
        foreach (Cove v in _coastCoves)
            Num($"  accepted cove cell=({v.I},{v.K}) shore point=({v.Oa:F0},{v.Oz:F0}) water width={2 * v.Rw:F0} m depth={v.D:F1} m beach={v.Wb:F0} m cliff weight={v.Wcl:F2} rho/Rw min={v.RhoRange().Min:F2} max={v.RhoRange().Max:F2}");

        var before = MakeScene(Terms.None); var after = MakeScene(_all);
        // Coast plan, 16 m/px, 1000 px.
        Grid gb = BuildPlan(before, win.A, win.Z, 16000, 1000), ga = BuildPlan(after, win.A, win.Z, 16000, 1000);
        double contour = _cfg.contour > 0 ? _cfg.contour : _cfg.contour_coast;
        SavePlan(gb, "coast_plan_before.png", contour, 1000); SavePlan(ga, "coast_plan_after.png", contour, 1000);
        // Natural coastline length: shoreline grid crossings x cell size.
        int cross = 0;
        for (int j = 0; j < gb.Nz - 1; j++) for (int i = 0; i < gb.Nx - 1; i++)
            {
                bool w = gb.H[j * gb.Nx + i] > 0;
                if (w != (gb.H[j * gb.Nx + i + 1] > 0)) cross++;
                if (w != (gb.H[(j + 1) * gb.Nx + i] > 0)) cross++;
            }
        double lenKm = cross * gb.Step / 1000d;
        Num($"coast window natural coastline length ~ {lenKm:F1} km (shoreline grid crossings x 16 m); accepted coves per 100 km of natural coastline = {100.0 * _coastCoves.Count / Math.Max(1e-9, lenKm):F1}");

        // Timing: ms per sample in the coast window, land points, all terms vs none, single thread.
        var tcx = Rent();
        var pts = new List<(double, double)>();
        for (int n = 0; pts.Count < 20000 && n < 400000; n++)
        {
            double a = win.A - half + Hash01(n, 0, 55, 0) * 2 * half, z = win.Z - half + Hash01(n, 1, 55, 1) * 2 * half;
            if (tcx.Height(before, a, z) > 0) pts.Add((a, z));
        }
        foreach (var (a, z) in pts.Take(2000)) tcx.Eval(after, a, z, false); // warm
        var tsw = Stopwatch.StartNew(); foreach (var (a, z) in pts) tcx.Eval(before, a, z, false); double tNone = tsw.Elapsed.TotalMilliseconds / pts.Count;
        tsw.Restart(); foreach (var (a, z) in pts) tcx.Eval(after, a, z, false); double tAll = tsw.Elapsed.TotalMilliseconds / pts.Count;
        Return(tcx);
        Num($"cost: {tNone * 1000:F1} us/sample terms none, {tAll * 1000:F1} us/sample all terms (single thread, {pts.Count} land points in the coast window, cove cells prewarmed; shore distance NOT cached: 4 natural samples per near-shore point)");

        // Forced-size cove pictures.
        Num($"FORCED cove widths for pictures only (not accepted-by-rule sizes): {string.Join(",", forcedWidths)} assigned to the first accepted coves ordered by distance from the window centre");
        var forcedCoves = new List<Cove>();
        for (int n = 0; n < Math.Min(forcedWidths.Length, _coastCoves.Count); n++)
        {
            Cove f = _coastCoves[n].Clone(); f.Forced = true; f.Build(_cfg, forcedWidths[n] / 2d); forcedCoves.Add(f);
            Num($"  forced cove {n + 1}: cell=({f.I},{f.K}) water width {forcedWidths[n]:F0} m (natural {2 * _coastCoves[n].Rw:F0} m) depth {f.D:F1} m beach {f.Wb:F0} m wall {Math.Atan(f.TanAlpha) * 180 / Math.PI:F0} deg");
        }
        foreach (Cove f in forcedCoves)
        {
            int w = (int)Math.Round(2 * f.Rw);
            var fs = MakeScene(_all, null, _coveBase.Derive(new Dictionary<(int, int), Cove> { [(f.I, f.K)] = f }));
            Grid gp = BuildPlan(fs, f.Ca, f.Cz, 5000, 1000), gp0 = BuildPlan(before, f.Ca, f.Cz, 5000, 1000);
            SavePlan(gp0, $"coast_plan_cove_{w}_before.png", contour, 1000); SavePlan(gp, $"coast_plan_cove_{w}_after.png", contour, 1000);
            double dist = Math.Max(_cfg.cove_cam_dist, 1.2 * w);
            double ca = f.Ca - f.Nx * dist, cz = f.Cz - f.Nz * dist, yaw = Math.Atan2(f.Nz, f.Nx);
            RenderOblique(before, ca, cz, yaw, 120, -4, $"coast_oblique_cove_{w}_before.png", 0, 0);
            RenderOblique(fs, ca, cz, yaw, 120, -4, $"coast_oblique_cove_{w}_after.png", 0, 0);
        }
        foreach (Cove f in forcedCoves)
        {
            int w = (int)Math.Round(2 * f.Rw);
            var fs = MakeScene(_all, null, _coveBase.Derive(new Dictionary<(int, int), Cove> { [(f.I, f.K)] = f }));
            double dist = w + 400, ca = f.Ca - f.Nx * dist, cz = f.Cz - f.Nz * dist, yaw = Math.Atan2(f.Nz, f.Nx), pitch = -Math.Atan(25 / dist) * 180 / Math.PI;
            RenderOblique(before, ca, cz, yaw, 25, pitch, $"coast_oblique_cove_{w}_low_before.png", 3000);
            RenderOblique(fs, ca, cz, yaw, 25, pitch, $"coast_oblique_cove_{w}_low_after.png", 3000);
        }
        if (_coastCoves.Count > 0)
        {
            double nxm = _coastCoves.Average(v => v.Nx), nzm = _coastCoves.Average(v => v.Nz), nl = Math.Sqrt(nxm * nxm + nzm * nzm); nxm /= nl; nzm /= nl;
            double tga = _coastCoves.Average(v => v.Oa), tgz = _coastCoves.Average(v => v.Oz);
            double ca = tga - nxm * 6000, cz = tgz - nzm * 6000, yaw = Math.Atan2(nzm, nxm);
            RenderOblique(before, ca, cz, yaw, 1500, -14, "coast_overview_oblique_before.png", 3000);
            RenderOblique(after, ca, cz, yaw, 1500, -14, "coast_overview_oblique_after.png", 3000);
        }
        // R9-style statistics: coast-window land within 1.5 km of the shore, 2D slope at 4 m and the RMS of the T7 delta (after - before).
        {
            var rpts = new List<(double, double)>(); var ptsLock = new object();
            for (int n = 0; rpts.Count < 20000 && n < 40; n++)
            {
                var chunk = new List<(double, double)>[64];
                ParFor(64, (cx, b) =>
                {
                    var l = new List<(double, double)>();
                    for (int q = 0; q < 2000; q++)
                    {
                        int idx = (n * 64 + b) * 2000 + q;
                        double a = win.A - half + Hash01(idx, 0, 66, 0) * 2 * half, z = win.Z - half + Hash01(idx, 1, 66, 1) * 2 * half;
                        var d = cx.M.SampleDiagnostics(a, z);
                        if (d.Height > 0 && cx.ShoreDist(a, z, d.Continentalness) <= 1500) l.Add((a, z));
                    }
                    chunk[b] = l;
                });
                foreach (var l in chunk) rpts.AddRange(l);
            }
            if (rpts.Count > 20000) rpts = rpts.Take(20000).ToList();
            var sb = new double[rpts.Count]; var sa = new double[rpts.Count]; var dl = new double[rpts.Count];
            ParFor(rpts.Count, (cx, n) =>
            {
                var (a, z) = rpts[n];
                double Sl(Scene sc, out double h0)
                {
                    h0 = cx.Height(sc, a, z);
                    double gx = (cx.Height(sc, a + 2, z) - cx.Height(sc, a - 2, z)) / 4, gz = (cx.Height(sc, a, z + 2) - cx.Height(sc, a, z - 2)) / 4;
                    return Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180 / Math.PI;
                }
                sb[n] = Sl(before, out double hb); sa[n] = Sl(after, out double ha); dl[n] = ha - hb;
            });
            var lb = sb.OrderBy(x => x).ToList(); var la = sa.OrderBy(x => x).ToList();
            Num($"R9-style coast window land within 1.5 km of shore ({rpts.Count} points): 2D slope 4 m p50/p95 deg before {Pct(lb, .5):F2}/{Pct(lb, .95):F2}, after {Pct(la, .5):F2}/{Pct(la, .95):F2}; RMS of T7 delta (after-before height) {Math.Sqrt(dl.Sum(x => x * x) / Math.Max(1, dl.Length)):F3} m; mean {dl.Average():F3} m; p95 |delta| {Pct(dl.Select(Math.Abs).OrderBy(x => x).ToList(), .95):F2} m");
        }
        // Cliff oblique: the accepted coves of the window with the highest cliff weight; camera 400 m off a stretch 1.5 km along the coast, looking along it at the cove.
        // Revised: the shore point (land, within 300 m of the shoreline by c/|grad c|) with cliff weight > 0.5 nearest to an accepted cove; camera 400 m seaward and 600 m back along the coast, aimed at the shore 500 m further along towards the cove.
        if (_coastCoves.Count > 0)
        {
            var pc = Rent();
            double bestD = double.MaxValue, bpa = 0, bpz = 0; Cove? bcv = null;
            for (double a = win.A - half; a <= win.A + half; a += 100)
                for (double z = win.Z - half; z <= win.Z + half; z += 100)
                {
                    double cc = pc.M.SampleNaturalDiagnostics(a, z).Continentalness;
                    if (cc < 0 || cc > 0.06 || pc.WclAt(_cfg, a, z) <= 0.5) continue;
                    pc.Grad(a, z, out _, out double gx, out double gz);
                    double ds = cc / Math.Sqrt(gx * gx + gz * gz);
                    if (ds > 300) continue;
                    foreach (Cove v in _coastCoves)
                    {
                        double dd = Math.Sqrt((v.Oa - a) * (v.Oa - a) + (v.Oz - z) * (v.Oz - z));
                        if (dd < bestD) { bestD = dd; bpa = a; bpz = z; bcv = v; }
                    }
                }
            if (bcv != null)
            {
                pc.Grad(bpa, bpz, out _, out double gx, out double gz);
                double gl = Math.Sqrt(gx * gx + gz * gz), nx = gx / gl, nz = gz / gl, tx = -nz, tz = nx;
                if (tx * (bcv.Oa - bpa) + tz * (bcv.Oz - bpz) < 0) { tx = -tx; tz = -tz; }
                double ca = bpa - nx * 400 - tx * 600, cz = bpz - nz * 400 - tz * 600, yaw = Math.Atan2(bpz + tz * 500 - cz, bpa + tx * 500 - ca); // aim at the shore point 500 m further along the coast
                Num($"cliff oblique: cliff shore point ({bpa:F0},{bpz:F0}) cliff weight {pc.WclAt(_cfg, bpa, bpz):F2}, {bestD:F0} m from accepted cove cell=({bcv.I},{bcv.K}); camera ({ca:F0},{cz:F0}) yaw {yaw * 180 / Math.PI:F0} deg, 40 m above sea");
                Return(pc);
                RenderOblique(before, ca, cz, yaw, 40, -2, "cliff_oblique_before.png", 0, 0);
                RenderOblique(after, ca, cz, yaw, 40, -2, "cliff_oblique_after.png", 0, 0);
            }
            else { Return(pc); Num("cliff oblique skipped: no cliffed shore point in the window"); }
        }
    }

    // ---- hummock country -----------------------------------------------------------------------------------------------------------------------------

    void HummockStage(( double A, double Z) eye, bool forceHum)
    {
        (double A, double Z)? hit = null;
        double dist = 0;
        for (int ring = 0; ring <= 300 && hit == null; ring++)
        {
            double r = ring * 1000d;
            var pts = new List<(double, double)>();
            if (ring == 0) pts.Add(eye);
            else
                for (double t = -r; t < r; t += 500)
                {
                    pts.Add((eye.A + t, eye.Z - r)); pts.Add((eye.A + r, eye.Z + t)); pts.Add((eye.A - t, eye.Z + r)); pts.Add((eye.A - r, eye.Z - t));
                }
            pts = pts.Where(p => p.Item2 > 1000 && p.Item2 < WorldWidth - 1000).Select(p => ((p.Item1 % Circumference + Circumference) % Circumference, p.Item2)).ToList();
            var res = new (double, double, double)[pts.Count]; var ok = new bool[pts.Count];
            ParFor(pts.Count, (cx, n) =>
            {
                var (a, z) = pts[n];
                var d = cx.M.SampleDiagnostics(a, z);
                if (d.Height <= 1 || Elig(d) < 0.5 || d.SiteWeight > 0 || d.WeatherLungWeight > 0) return;
                if ((cx.Op?.CorridorWeight(a, z, 0) ?? 0) > 0.5) return;
                double ds = cx.ShoreDist(a, z, d.Continentalness);
                if (cx.WhAt(_cfg, a, z, d.Ruggedness, ds) >= 0.9) { ok[n] = true; res[n] = (a, z, Math.Sqrt((a - eye.A) * (a - eye.A) + (z - eye.Z) * (z - eye.Z))); }
            });
            double bd = double.MaxValue;
            for (int n = 0; n < pts.Count; n++) if (ok[n] && res[n].Item3 < bd) { bd = res[n].Item3; hit = (res[n].Item1, res[n].Item2); dist = bd; }
        }
        if (hit == null) { _ok = false; Num("ESCALATE: no w_h>=0.9 point within 300 km of plains_eye"); return; }
        var p0 = hit.Value;
        Num($"HUMMOCK COUNTRY nearest w_h>=0.9 point to plains_eye: around={p0.A:F0} across={p0.Z:F0} ({dist / 1000:F1} km away)");

        // Style map, 60 km at 60 m/px.
        var smap = St("style") ? BuildStyleMap(p0.A, p0.Z, 60000, 1000) : Array.Empty<byte>();
        if (St("style")) SaveRgb(smap, 1000, 1000, "style_map.png");
        if (!St("hummock")) return;

        var before = MakeScene(Terms.None);
        double baseRatio = _cfg.hummock_height / _cfg.hummock_spacing;
        Grid gb = BuildPlan(before, p0.A, p0.Z, 2000, 1000);
        var statCx = Rent();
        foreach (double L in new[] { 120d, 180d, 260d })
        {
            Cfg c = _cfg.Clone(); c.hummock_spacing = L; c.hummock_height = baseRatio * L;
            var after = MakeScene(_all, c);
            string ln = ((int)L).ToString();
            Grid ga = BuildPlan(after, p0.A, p0.Z, 2000, 1000);
            double contour = _cfg.contour > 0 ? _cfg.contour : _cfg.contour_hummock;
            SavePlan(gb, $"hummocks_plan_L{ln}_before.png", contour, 1000); SavePlan(ga, $"hummocks_plan_L{ln}_after.png", contour, 1000);
            if (St("plains")) foreach ((double h, double pitch, string tag) in new[] { (1.7, 0d, "h1.7"), (30d, -8d, "h30") })
            {
                RenderOblique(before, p0.A, p0.Z, 0, h, pitch, $"hummocks_oblique_L{ln}_{tag}_before.png", 0);
                RenderOblique(after, p0.A, p0.Z, 0, h, pitch, $"hummocks_oblique_L{ln}_{tag}_after.png", 0);
            }
            // Slope stats and ponds over the 2 km window.
            var sNone = WindowSlope(before, p0.A, p0.Z); var sAfter = WindowSlope(after, p0.A, p0.Z);
            var sBase = WindowSlope(MakeScene(new Terms(true, false, false, false, false), c), p0.A, p0.Z);
            var sForced = WindowSlope(MakeScene(new Terms(true, true, false, false, true), c), p0.A, p0.Z);
            Num($"hummock window L={ln} H={c.hummock_height:F1}: 2D slope at 4 m p50/p95 deg: none {sNone.P50:F2}/{sNone.P95:F2}; base {sBase.P50:F2}/{sBase.P95:F2}; all terms {sAfter.P50:F2}/{sAfter.P95:F2}; base+hummock w_h=1 {sForced.P50:F2}/{sForced.P95:F2}");
            // Ponds in the 2 km window.
            var ponds = new List<Pond>();
            double L2 = c.hummock_spacing;
            for (int i = (int)Math.Floor((p0.A - 1000) / L2); i <= (int)Math.Floor((p0.A + 1000) / L2); i++)
                for (int k = (int)Math.Floor((p0.Z - 1000) / L2); k <= (int)Math.Floor((p0.Z + 1000) / L2); k++)
                {
                    Ctx.HumCell(c, i, k, out int type, out double ca, out double cz, out _, out _, out _, out _);
                    if (type != 2 || Math.Abs(ca - p0.A) > 1000 || Math.Abs(cz - p0.Z) > 1000) continue;
                    Pond? p = statCx.GetPond(after, i, k); if (p != null) ponds.Add(p);
                }
            var areas = new List<double>();
            foreach (Pond p in ponds)
            {
                int n = 0;
                for (double u = -p.R; u <= p.R; u += 4) for (double v = -p.R; v <= p.R; v += 4)
                    {
                        double co = Math.Cos(p.Ori), si = Math.Sin(p.Ori);
                        double wa = p.Ca + u * co - v * si, wz = p.Cz + u * si + v * co;
                        if (Ctx.Q2(wa - p.Ca, wz - p.Cz, p.R, p.Ratio, p.Ori) < 1 && statCx.Height(after, wa, wz) < p.Level) n++;
                    }
                areas.Add(n * 16d);
            }
            areas.Sort();
            Num($"hummock window L={ln}: ponds {ponds.Count} in 4 km2 = {ponds.Count / 4.0:F1} per km2; area m2 median {(areas.Count > 0 ? areas[areas.Count / 2] : 0):F0} max {(areas.Count > 0 ? areas[^1] : 0):F0}");
        }
        Return(statCx);
    }

    // ---- statistics on Plains anchors ----------------------------------------------------------------------------------------------------------------

    static double RangePower(double[] power, double span, double lo, double hi)
    {
        double sum = 0;
        for (int k = 1; k < power.Length; k++) { double lambda = span / k; if (lambda >= lo && lambda < hi) sum += power[k]; }
        return sum;
    }

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    static double Median(List<double> v) { v = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return v.Count == 0 ? double.NaN : v[v.Count / 2]; }

    (double P50, double P95) WindowSlope(Scene s, double ca, double cz)
    {
        const int n = 100; var slopes = new double[n * n];
        ParFor(n, (cx, j) =>
        {
            for (int i = 0; i < n; i++)
            {
                double a = ca - 1000 + (i + 0.5) * 20, z = cz - 1000 + (j + 0.5) * 20;
                double h1 = cx.Height(s, a + 2, z), h2 = cx.Height(s, a - 2, z), h3 = cx.Height(s, a, z + 2), h4 = cx.Height(s, a, z - 2);
                double gx = (h1 - h2) / 4, gz = (h3 - h4) / 4;
                slopes[j * n + i] = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180 / Math.PI;
            }
        });
        var l = slopes.OrderBy(x => x).ToList();
        return (Pct(l, .5), Pct(l, .95));
    }

    void PlainsStats(bool forceHum)
    {
        string json = File.ReadAllText(ProjectSettings.GlobalizePath("res://.godot/audit_baseline/common/t7/relief/g1_landonly_base/relief_spectrum.json"));
        using var doc = JsonDocument.Parse(json);
        var anchors = new List<(double, double)>();
        foreach (var cls in doc.RootElement.GetProperty("classes").EnumerateArray())
            if (cls.GetProperty("class").GetString() == "Plains")
                foreach (var p in cls.GetProperty("anchor_positions").EnumerateArray()) { if (anchors.Count < 24) anchors.Add((p[0].GetDouble(), p[1].GetDouble())); }
        Num($"RELIEF STATISTICS on {anchors.Count} Plains anchors (relief_spectrum.json g1_landonly_base). 2D slope: 4 m central differences, 20x20 grid at 100 m per anchor; decade RMS from 4096 x 1 m transects, both directions");
        var configs = new (string Name, Terms T)[]
        {
            ("terms none", Terms.None),
            ("base only", new Terms(true, false, false, false, false)),
            ("base+hummock w_h=1 (forced)", new Terms(true, true, false, false, true)),
        };
        foreach (var (name, t) in configs)
        {
            var sc = MakeScene(t);
            var pooled = new List<double>[anchors.Count]; var d1 = new double[anchors.Count][]; var d2 = new double[anchors.Count][];
            ParFor(anchors.Count, (cx, ai) =>
            {
                var (a, z) = anchors[ai];
                var sl = new List<double>();
                for (int u = 0; u < 20; u++) for (int v = 0; v < 20; v++)
                    {
                        double pa = a + (u - 9.5) * 100, pz = z + (v - 9.5) * 100;
                        double h1 = cx.Height(sc, pa + 2, pz), h2 = cx.Height(sc, pa - 2, pz), h3 = cx.Height(sc, pa, pz + 2), h4 = cx.Height(sc, pa, pz - 2);
                        double gx = (h1 - h2) / 4, gz = (h3 - h4) / 4;
                        sl.Add(Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180 / Math.PI);
                    }
                pooled[ai] = sl; d1[ai] = new double[2]; d2[ai] = new double[2];
                for (int dir = 0; dir < 2; dir++)
                {
                    double[] h = new double[4096];
                    for (int n = 0; n < 4096; n++) h[n] = dir == 0 ? cx.Height(sc, a - 2048 + n, z) : cx.Height(sc, a, z - 2048 + n);
                    double[] spec = LandscapeReliefAudit.Spectrum(h);
                    d1[ai][dir] = Math.Sqrt(RangePower(spec, 4096, 2, 30)); d2[ai][dir] = Math.Sqrt(RangePower(spec, 4096, 30, 300));
                }
            });
            var all = pooled.SelectMany(x => x).OrderBy(x => x).ToList();
            double med50 = Median(pooled.Select(l => Pct(l.OrderBy(x => x).ToList(), .5)).ToList()), med95 = Median(pooled.Select(l => Pct(l.OrderBy(x => x).ToList(), .95)).ToList());
            Num($"  [{name}] slope p50/p95 deg pooled {Pct(all, .5):F2}/{Pct(all, .95):F2}; median of anchors {med50:F2}/{med95:F2}; decade RMS 2-30 m median {Median(d1.SelectMany(x => x).ToList()):F3} m, 30-300 m median {Median(d2.SelectMany(x => x).ToList()):F3} m");
        }
    }

    // ---- grids ---------------------------------------------------------------------------------------------------------------------------------------

    sealed class Grid
    {
        public double Pa, Pz, Ax = 1, Az = 0, Bx = 0, Bz = 1, U0, V0, Step;
        public int Nx, Nz;
        public float[] H = Array.Empty<float>(), WL = Array.Empty<float>(); public byte[] K = Array.Empty<byte>();
        public float[] C = Array.Empty<float>();
    }

    Grid BuildGrid(Scene s, Grid g)
    {
        int n = g.Nx * g.Nz;
        g.H = new float[n]; g.WL = new float[n]; g.K = new byte[n];
        ParFor(g.Nz, (cx, j) =>
        {
            double v = g.V0 + j * g.Step;
            for (int i = 0; i < g.Nx; i++)
            {
                double u = g.U0 + i * g.Step;
                Smp m = cx.Eval(s, g.Pa + g.Ax * u + g.Bx * v, g.Pz + g.Az * u + g.Bz * v, true);
                int idx = j * g.Nx + i; g.H[idx] = m.H; g.WL[idx] = m.WL; g.K[idx] = m.K;
            }
        });
        return g;
    }

    Grid BuildPlan(Scene s, double ca, double cz, double size, int px)
    {
        double step = size / px;
        var g = new Grid { Pa = 0, Pz = 0, U0 = ca - size / 2 + step / 2, V0 = cz - size / 2 + step / 2, Step = step, Nx = px, Nz = px };
        BuildGrid(s, g);
        double sa = 315 * Math.PI / 180, el = 40 * Math.PI / 180;
        Shade(g, Math.Sin(sa) * Math.Cos(el), Math.Cos(sa) * Math.Cos(el), Math.Sin(el), _cfg.plan_exag);
        return g;
    }

    static readonly double[] Sand = { 0.85, 0.78, 0.55 }, GreenLo = { 0.33, 0.53, 0.24 }, GreenHi = { 0.52, 0.52, 0.30 }, Rock = { 0.52, 0.47, 0.42 };
    static readonly double[] WShallow = { 0.30, 0.58, 0.72 }, WDeep = { 0.04, 0.14, 0.34 };
    static readonly double[] SkyHorizon = { 0.72, 0.80, 0.90 }, SkyTop = { 0.28, 0.48, 0.85 };

    static bool NearWater(Grid g, int i, int j)
    {
        foreach (double r in new[] { 40d, 100d })
        {
            int n = Math.Max(1, (int)Math.Round(r / g.Step));
            for (int d = 0; d < 8; d++)
            {
                int x = i + (int)Math.Round(n * Math.Cos(d * Math.PI / 4)), y = j + (int)Math.Round(n * Math.Sin(d * Math.PI / 4));
                if (x >= 0 && y >= 0 && x < g.Nx && y < g.Nz && g.K[y * g.Nx + x] != 0) return true;
            }
        }
        return false;
    }

    static void Shade(Grid g, double sx, double sz, double se, double exag)
    {
        int nx = g.Nx, nz = g.Nz;
        g.C = new float[nx * nz * 3];
        Parallel.For(0, nz, j =>
        {
            for (int i = 0; i < nx; i++)
            {
                int idx = j * nx + i;
                double h = g.H[idx];
                double[] col;
                if (g.K[idx] != 0)
                {
                    double depth = g.WL[idx] - h;
                    double t = 1 - Math.Exp(-depth / 8d);
                    col = new[] { Lerp(WShallow[0], WDeep[0], t), Lerp(WShallow[1], WDeep[1], t), Lerp(WShallow[2], WDeep[2], t) };
                }
                else
                {
                    int il = Math.Max(0, i - 1), ir = Math.Min(nx - 1, i + 1), jl = Math.Max(0, j - 1), jr = Math.Min(nz - 1, j + 1);
                    double dhi = (g.H[j * nx + ir] - g.H[j * nx + il]) / ((ir - il) * g.Step), dhj = (g.H[jr * nx + i] - g.H[jl * nx + i]) / ((jr - jl) * g.Step);
                    double ga = dhi * g.Ax + dhj * g.Bx, gz = dhi * g.Az + dhj * g.Bz;
                    double slope = Math.Atan(Math.Sqrt(ga * ga + gz * gz)) * 180 / Math.PI;
                    double ea = ga * exag, ez = gz * exag, inv = 1 / Math.Sqrt(ea * ea + ez * ez + 1);
                    double lam = Math.Max(0, (-ea * sx - ez * sz + se) * inv);
                    double inten = Clamp(0.3 + 0.7 * lam / se, 0.15, 1.35);
                    double sandW = (1 - S(10, 14, slope)) * (1 - S(1.0, 2.0, h)); if (sandW > 0 && (h <= 0 || !NearWater(g, i, j))) sandW = 0;
                    double rockW = S(32, 38, slope), hw = S(30, 300, h);
                    col = new double[3];
                    for (int c = 0; c < 3; c++)
                    {
                        double gr = Lerp(GreenLo[c], GreenHi[c], hw);
                        double v = Lerp(Lerp(gr, Sand[c], sandW), Rock[c], rockW);
                        col[c] = v * inten;
                    }
                }
                for (int c = 0; c < 3; c++) g.C[idx * 3 + c] = (float)col[c];
            }
        });
    }

    // ---- images --------------------------------------------------------------------------------------------------------------------------------------

    static byte B(double v) => (byte)Math.Round(Clamp(v, 0, 1) * 255);

    void SaveRgb(byte[] rgb, int w, int h, string name)
    {
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
        Error e = img.SavePng(Path.Combine(_out, name));
        if (e != Error.Ok) { _ok = false; Num("PNG save failed " + name + " " + e); }
    }

    void SavePlan(Grid g, string name, double contour, int px)
    {
        byte[] rgb = new byte[px * px * 3];
        for (int py = 0; py < px; py++)
        {
            int j = px - 1 - py;
            for (int i = 0; i < px; i++)
            {
                int idx = j * px + i; double dark = 1;
                if (g.K[idx] == 0 && g.H[idx] > 0 && contour > 0)
                {
                    int ir = Math.Min(px - 1, i + 1), jd = Math.Max(0, j - 1);
                    double f0 = Math.Floor(g.H[idx] / contour);
                    if (f0 != Math.Floor(g.H[j * px + ir] / contour) || f0 != Math.Floor(g.H[jd * px + i] / contour)) dark = 0.62;
                }
                int o = (py * px + i) * 3;
                for (int c = 0; c < 3; c++) rgb[o + c] = B(g.C[idx * 3 + c] * dark);
            }
        }
        // Scale bar: 1 km (200 m when the window is under 3 km), bottom-left.
        double bar = g.Step * px < 3000 ? 200 : 1000;
        int len = (int)Math.Round(bar / g.Step), x0 = 20, y0 = px - 28;
        for (int y = y0 - 1; y <= y0 + 5; y++)
            for (int x = x0 - 1; x <= x0 + len + 1; x++)
            {
                if (x < 0 || x >= px || y < 0 || y >= px) continue;
                bool inner = x >= x0 && x < x0 + len && y >= y0 && y < y0 + 4;
                int o = (y * px + x) * 3; byte v = (byte)(inner ? 255 : 0);
                rgb[o] = rgb[o + 1] = rgb[o + 2] = v;
            }
        SaveRgb(rgb, px, px, name);
    }

    byte[] BuildStyleMap(double ca, double cz, double size, int px)
    {
        double step = size / px; var rgb = new byte[px * px * 3];
        ParFor(px, (cx, py) =>
        {
            double z = cz + size / 2 - (py + 0.5) * step;
            for (int i = 0; i < px; i++)
            {
                double a = ca - size / 2 + (i + 0.5) * step;
                var d = cx.M.SampleDiagnostics(a, z);
                double[] col;
                if (d.Height <= 0) col = new[] { 0.10, 0.25, 0.50 };
                else
                {
                    double ds = cx.ShoreDist(a, z, d.Continentalness);
                    double wh = cx.WhAt(_cfg, a, z, d.Ruggedness, ds), wcl = cx.WclAt(_cfg, a, z);
                    col = new[] { 0.80, 0.80, 0.78 };
                    if (d.PlateauWeight > 0.5) col = new[] { 0.62, 0.55, 0.48 };
                    if (!double.IsPositiveInfinity(ds)) { col = new[] { Lerp(col[0], 0.95, 0.6), Lerp(col[1], 0.85, 0.6), Lerp(col[2], 0.45, 0.6) }; if (wcl > 0) col = new[] { Lerp(col[0], 0.85, wcl), Lerp(col[1], 0.15, wcl), Lerp(col[2], 0.15, wcl) }; }
                    if (wh > 0) col = new[] { Lerp(col[0], 0.10, wh), Lerp(col[1], 0.62, wh), Lerp(col[2], 0.20, wh) };
                }
                int o = (py * px + i) * 3;
                for (int c = 0; c < 3; c++) rgb[o + c] = B(col[c]);
            }
        });
        // 10 km scale bar (white).
        int len = (int)(10000 / step), y0 = px - 28;
        for (int y = y0; y < y0 + 4; y++) for (int x = 20; x < 20 + len; x++) { int o = (y * px + x) * 3; rgb[o] = rgb[o + 1] = rgb[o + 2] = 255; }
        return rgb;
    }

    // ---- oblique voxel renderer ----------------------------------------------------------------------------------------------------------------------

    void RenderOblique(Scene s, double ca, double cz, double yaw, double eyeH, double pitchDeg, string name, double nearRange, int dummy = 0)
    {
        var sw = Stopwatch.StartNew();
        const int W = 1600, Hh = 900;
        double fa = Math.Cos(yaw), fz = Math.Sin(yaw), ra = Math.Sin(yaw), rz = -Math.Cos(yaw);
        var probe = Rent(); double ground = probe.Height(s, ca, cz); Return(probe);
        double camZ = Math.Max(ground, 0) + eyeH;
        var far = new Grid { Pa = ca, Pz = cz, Ax = fa, Az = fz, Bx = ra, Bz = rz, U0 = -200, V0 = -7200, Step = 8, Nx = 1525, Nz = 1800 };
        if (nearRange <= 0) nearRange = 2000;
        double nh = nearRange * 0.6 + 150;
        var near = new Grid { Pa = ca, Pz = cz, Ax = fa, Az = fz, Bx = ra, Bz = rz, U0 = -100, V0 = -nh, Step = 2, Nx = (int)((nearRange + 100) / 2), Nz = (int)(2 * nh / 2) };
        BuildGrid(s, far); BuildGrid(s, near);
        // Sun: behind-left of the camera, elevation 35.
        double sh = 1 / Math.Sqrt(0.4 * 0.4 + 0.9 * 0.9), el = 35 * Math.PI / 180;
        double sxa = (-0.4 * fa - 0.9 * ra) * sh * Math.Cos(el), sxz = (-0.4 * fz - 0.9 * rz) * sh * Math.Cos(el);
        Shade(far, sxa, sxz, Math.Sin(el), 1); Shade(near, sxa, sxz, Math.Sin(el), 1);
        double fpx = (W / 2d) / Math.Tan(30 * Math.PI / 180), yh = Hh / 2d + fpx * Math.Tan(pitchDeg * Math.PI / 180);
        byte[] rgb = new byte[W * Hh * 3];
        Parallel.For(0, W, x =>
        {
            double tanPhi = (x + 0.5 - W / 2d) / fpx;
            double[] col = new double[3];
            // sky
            for (int y = 0; y < Hh; y++)
            {
                double t = Clamp((yh - y) / (Hh * 0.7), 0, 1);
                int o = (y * W + x) * 3;
                for (int c = 0; c < 3; c++) rgb[o + c] = B(Lerp(SkyHorizon[c], SkyTop[c], Math.Pow(t, 0.7)));
            }
            int ybuf = Hh;
            double u = 0.5;
            while (u < 12000 && ybuf > 0)
            {
                double v = u * tanPhi;
                Grid g = near;
                double fi = (u - g.U0) / g.Step, fj = (v - g.V0) / g.Step;
                if (fi < 0 || fj < 0 || fi >= g.Nx - 1 || fj >= g.Nz - 1) { g = far; fi = (u - g.U0) / g.Step; fj = (v - g.V0) / g.Step; if (fi < 0 || fj < 0 || fi >= g.Nx - 1 || fj >= g.Nz - 1) break; }
                int i0 = (int)fi, j0 = (int)fj; double tx = fi - i0, ty = fj - j0;
                int p00 = j0 * g.Nx + i0;
                double h = (g.H[p00] * (1 - tx) + g.H[p00 + 1] * tx) * (1 - ty) + (g.H[p00 + g.Nx] * (1 - tx) + g.H[p00 + g.Nx + 1] * tx) * ty;
                int pn = (j0 + (ty > 0.5 ? 1 : 0)) * g.Nx + i0 + (tx > 0.5 ? 1 : 0);
                double dist = Math.Sqrt(u * u + v * v);
                double top = h;
                if (g.K[pn] != 0) { top = g.WL[pn]; double dz = camZ - top; double sinT = Clamp(dz / Math.Sqrt(dist * dist + dz * dz), 0, 1); double fr = 0.04 + 0.96 * Math.Pow(1 - sinT, 5); for (int c = 0; c < 3; c++) col[c] = Lerp(g.C[pn * 3 + c], SkyHorizon[c], Clamp(fr, 0, 1)); }
                else for (int c = 0; c < 3; c++)
                        col[c] = (g.C[p00 * 3 + c] * (1 - tx) + g.C[(p00 + 1) * 3 + c] * tx) * (1 - ty) + (g.C[(p00 + g.Nx) * 3 + c] * (1 - tx) + g.C[(p00 + g.Nx + 1) * 3 + c] * tx) * ty;
                double hz = 1 - Math.Exp(-dist / 12000d);
                for (int c = 0; c < 3; c++) col[c] = Lerp(col[c], SkyHorizon[c], hz);
                double yt = yh - fpx * (top - camZ) / u;
                int ys = Math.Max(0, (int)Math.Ceiling(yt));
                if (ys < ybuf)
                {
                    for (int y = ys; y < ybuf; y++) { int o = (y * W + x) * 3; rgb[o] = B(col[0]); rgb[o + 1] = B(col[1]); rgb[o + 2] = B(col[2]); }
                    ybuf = ys;
                }
                u += Math.Max(0.5, u * 0.0015);
            }
        });
        SaveRgb(rgb, W, Hh, name);
        Console.WriteLine($"T7P oblique {name} {sw.Elapsed.TotalSeconds:F1} s (camera {ca:F0},{cz:F0} yaw {yaw * 180 / Math.PI:F0} h {eyeH} above {(ground > 0 ? "ground" : "sea")} {ground:F1})");
    }
}
