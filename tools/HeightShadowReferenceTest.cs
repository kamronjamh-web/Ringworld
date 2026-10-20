using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>
/// Shadow revamp S4b coverage (docs/shadow-revamp-plan.md S4, s4_spec.md section 9): the GPU march of ring_height_shadow_build.comp against a double-precision
/// CPU reference that uses the SAME lattice heights and bilinear reconstruction, with the ray from RingTerrainShadowGeometry.SampleStraightRay at step t/8 and
/// analytic structural walls. Fixtures: Gaussian ridge, cliff, cone near the window edge, bores, wall at 2 km, ridge across the ring seam, one real landform
/// mountain; plus low-sun curvature tips, seam equivalence and build timing. Needs a real Vulkan renderer (local RenderingDevice), not --headless.
/// </summary>
public partial class HeightShadowReferenceTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const int Width = 318_000;
    private const int N = RingHeightShadowLattice.TextureSize;
    private const int Level0Texel = 4;
    private static readonly double Radius = Circumference / Math.Tau;
    private static readonly int[] LevelTexel = { Level0Texel, Level0Texel * 4 };

    private int _checks;
    private readonly List<string> _failures = new();
    private string _only = "";

    // ---- fixtures ----

    private sealed class Fixture
    {
        public string Name = "";
        public long CamAround = 3_000_000;
        public int CamAcross = 100_000;
        public Func<long, int, float> Height = (_, _) => 0f;
        public IHeightShadowSource[]? Sources;
        public bool Walls;
        public float WallTop;
        public (double Elev, double Az)[] Suns = Array.Empty<(double, double)>();
        public Func<double, double, double>? Profile; // analytic height (around offset from the camera, across offset from CamAcross) for the unwindowed truth march
    }

    private sealed class Built
    {
        public RingHeightShadowClipmap Clip = null!;
        public readonly HeightShadowWindow[] Win = new HeightShadowWindow[2];
        public readonly float[] LevelMax = new float[2];
        public float OutsideMax;
        public float[][] H = null!;
        public long Anchor;
    }

    private static double SignedAround(long around, long cam)
    {
        double d = (((around - cam) % Circumference) + Circumference) % Circumference;
        return d >= Circumference / 2 ? d - Circumference : d;
    }

    private static double Micro(double da, double dc) => 3.0 * Math.Sin(da * 0.05) * Math.Cos(dc * 0.07);

    private static Fixture RidgeFixture(string name, long camAround)
    {
        return new Fixture
        {
            Name = name, CamAround = camAround,
            Height = (a, c) =>
            {
                double da = SignedAround(a, camAround), dc = c - 100_000;
                double x = da * Math.Cos(0.3) + dc * Math.Sin(0.3) - 1200.0;
                return (float)(100.0 + 500.0 * Math.Exp(-x * x / (2.0 * 120.0 * 120.0)) + Micro(da, dc));
            },
            Suns = new[] { (12.0, 15.0), (4.0, 60.0), (25.0, 330.0), (2.0, 200.0), (60.0, 10.0) },
        };
    }

    private static Fixture[] SyntheticFixtures()
    {
        const long cam = 3_000_000;
        return new[]
        {
            RidgeFixture("ridge", cam),
            new Fixture
            {
                Name = "cliff", CamAround = cam,
                Height = (a, c) =>
                {
                    double da = SignedAround(a, cam), dc = c - 100_000;
                    double x = da * Math.Cos(0.5) - dc * Math.Sin(0.5) - 800.0;
                    return (float)(100.0 + (x > 0 ? 350.0 : 0.0) + Micro(da, dc));
                },
                Suns = new[] { (10.0, 5.0), (3.0, 40.0), (25.0, 340.0), (1.5, 190.0), (50.0, 20.0) },
            },
            new Fixture
            {
                Name = "cone_edge", CamAround = cam,
                Height = (a, c) =>
                {
                    double da = SignedAround(a, cam), dc = c - 100_000;
                    double r = Math.Sqrt((da - 1800.0) * (da - 1800.0) + (dc - 300.0) * (dc - 300.0));
                    return (float)(100.0 + Math.Max(0.0, 700.0 * (1.0 - r / 500.0)) + Micro(da, dc));
                },
                Suns = new[] { (14.0, 350.0), (5.0, 20.0), (30.0, 0.0), (2.5, 160.0), (70.0, 200.0) },
            },
            new Fixture
            {
                Name = "bore", CamAround = cam,
                Height = (a, c) =>
                {
                    double da = SignedAround(a, cam), dc = c - 100_000;
                    double r1 = Math.Sqrt((da - 500.0) * (da - 500.0) + (dc + 200.0) * (dc + 200.0));
                    double r2 = Math.Sqrt((da + 700.0) * (da + 700.0) + (dc - 500.0) * (dc - 500.0));
                    if (r1 < 76.0 || r2 < 151.0) return 60f;
                    return (float)(250.0 + Micro(da, dc));
                },
                Suns = new[] { (20.0, 30.0), (8.0, 120.0), (35.0, 250.0), (3.0, 300.0), (80.0, 0.0) },
            },
            new Fixture
            {
                Name = "wall_2km", CamAround = cam, CamAcross = 2000, Walls = true, WallTop = 700f,
                Height = (a, c) => (float)(100.0 + Micro(SignedAround(a, cam), c)),
                Suns = new[] { (10.0, 250.0), (4.0, 280.0), (25.0, 300.0), (12.0, 60.0), (60.0, 270.0) },
            },
            RidgeFixture("ridge_seam", Circumference - 704),
        };
    }

    // ---- beyond-L1 fixtures (low sun: casters outside both windows, G < 0 partial results) ----

    private const double BeyondRidgeHeight = 250.0, BeyondRidgeAt = 1000.0, BeyondMassifHeight = 3000.0, BeyondMassifAt = 13_000.0;

    /// <summary>Floor 0 m, a 250 m Gaussian ridge 1 km sunward (inside L0) and a 3000 m Gaussian massif 13 km sunward (beyond the 8 km L1 half-width); s runs along the sun axis, t across it.</summary>
    private static double BeyondProfile(double s, double t)
    {
        double ridge = BeyondRidgeHeight * Math.Exp(-(s - BeyondRidgeAt) * (s - BeyondRidgeAt) / (2.0 * 60.0 * 60.0));
        double massif = BeyondMassifHeight * Math.Exp(-((s - BeyondMassifAt) * (s - BeyondMassifAt) / (2.0 * 1500.0 * 1500.0) + t * t / (2.0 * 8000.0 * 8000.0)));
        return ridge + massif;
    }

    private static Fixture[] BeyondFixtures()
    {
        const long cam = 3_000_000;
        const int camAcross = 100_000;
        return new[]
        {
            new Fixture
            {
                Name = "beyond_l1", CamAround = cam, CamAcross = camAcross,
                Profile = (da, dc) => BeyondProfile(da, dc),
                Height = (a, c) => (float)BeyondProfile(SignedAround(a, cam), c - camAcross),
                Suns = new[] { (8.0, 0.0), (15.0, 0.0) },
            },
            new Fixture
            {
                Name = "beyond_l1_across", CamAround = cam, CamAcross = camAcross,
                Profile = (da, dc) => BeyondProfile(dc, da),
                Height = (a, c) => (float)BeyondProfile(c - camAcross, SignedAround(a, cam)),
                Suns = new[] { (8.0, 90.0) },
            },
        };
    }

    // ---- build ----

    private Built BuildLevels(RingTerrainHeightShadowGpu gpu, Fixture f)
    {
        var b = new Built();
        IHeightShadowSource[] sources = f.Sources ?? new IHeightShadowSource[] { new SyntheticHeightShadowSource(Circumference, Width, f.Height) };
        b.Clip = new RingHeightShadowClipmap(Circumference, Width, Level0Texel);
        b.H = new float[2][];
        for (int l = 0; l < 2; l++)
        {
            b.Win[l] = b.Clip.WindowAt(l, f.CamAround, f.CamAcross);
            b.Clip.FillLevel(l, b.Win[l], sources, CancellationToken.None);
            gpu.UploadIndexRect(l, b.Clip, b.Win[l].Rect);
            gpu.BuildMips(l);
            b.H[l] = b.Clip.Levels[l].Heights;
            b.LevelMax[l] = RingTerrainHeightShadowGpu.LevelMaxHeight(b.Clip, l);
        }
        // Stand-in for the S4c pyramid QueryRegion bound (+200 m): the fixtures are flat or infinite-feature outside the L1 window, so the window maximum bounds them.
        b.OutsideMax = Math.Max(b.LevelMax[0], b.LevelMax[1]) + 200f;
        b.Anchor = RingHeightShadowLattice.AnchorMeters(f.CamAround, LevelTexel[1]);
        return b;
    }

    private static Vector3 SunVector(double camAround, double elevDeg, double azDeg)
    {
        double theta = camAround / Radius, e = elevDeg * Math.PI / 180.0, az = azDeg * Math.PI / 180.0;
        double up = Math.Sin(e), around = Math.Cos(e) * Math.Cos(az), across = Math.Cos(e) * Math.Sin(az);
        double c = Math.Cos(theta), s = Math.Sin(theta);
        return new Vector3((float)(-up * c - around * s), (float)across, (float)(-up * s + around * c));
    }

    private HeightShadowBuildInputs MakeInputs(Fixture f, Built b, Vector3 sun)
    {
        var inputs = new HeightShadowBuildInputs
        {
            RingRadiusMeters = Radius, WidthMeters = Width, Walls = f.Walls, WallTopMeters = f.WallTop, OutsideMaxMeters = b.OutsideMax,
            AnchorMeters = b.Anchor,
        };
        inputs.AnchorSun = RingHeightShadowLattice.ProjectToTangentFrame(sun.X, sun.Y, sun.Z, b.Anchor / Radius);
        for (int l = 0; l < 2; l++)
        {
            inputs.TexelMeters[l] = LevelTexel[l];
            inputs.Windows[l] = b.Win[l];
            inputs.LevelMax[l] = b.LevelMax[l];
        }
        return inputs;
    }

    private (float[] R, float[] G)[] RunBuild(RingTerrainHeightShadowGpu gpu, RenderingDevice rd, Fixture f, Built b, Vector3 sun)
    {
        gpu.SetBuildInputs(MakeInputs(f, b, sun));
        gpu.DispatchBuild(0);
        gpu.DispatchBuild(1);
        rd.Submit();
        rd.Sync();
        return new[] { gpu.ReadBuildOutput(0), gpu.ReadBuildOutput(1) };
    }

    // ---- reference ----

    private struct RefResult
    {
        public double R, DBest, SlopeTol;
        public bool Unresolved, SunDown, WallMismatch;
    }

    private static double SampleBilinear(Built b, int lev, double wx, double wy)
    {
        HeightShadowWindow win = b.Win[lev];
        double ux = wx + RingHeightShadowLattice.SlotOf(win.I0), uy = wy + RingHeightShadowLattice.SlotOf(win.J0);
        if (ux >= N) ux -= N;
        if (uy >= N) uy -= N;
        int x0 = (int)Math.Floor(ux), y0 = (int)Math.Floor(uy);
        double fx = ux - x0, fy = uy - y0;
        float[] h = b.H[lev];
        double a = h[(y0 & (N - 1)) * N + (x0 & (N - 1))], bb = h[(y0 & (N - 1)) * N + ((x0 + 1) & (N - 1))];
        double c = h[((y0 + 1) & (N - 1)) * N + (x0 & (N - 1))], d = h[((y0 + 1) & (N - 1)) * N + ((x0 + 1) & (N - 1))];
        double top = a + (bb - a) * fx, bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }

    private static bool InsideWindow(double wx, double wy) => wx >= 0.0 && wx <= N - 1 && wy >= 0.0 && wy <= N - 1;

    private static RefResult Trace(Built b, Fixture f, Vector3 sun, int lev, int ox, int oy)
    {
        HeightShadowWindow win = b.Win[lev];
        int t = LevelTexel[lev];
        long iIndex = win.I0 + ox, jIndex = win.J0 + oy;
        double t0 = b.H[lev][RingHeightShadowLattice.SlotOf(jIndex) * N + RingHeightShadowLattice.SlotOf(iIndex)];
        double a0 = iIndex * (double)t, c0 = jIndex * (double)t;
        var res = new RefResult();
        HeightShadowSunFrame frame = RingHeightShadowLattice.ProjectToTangentFrame(sun.X, sun.Y, sun.Z, a0 / Radius);
        if (frame.Up <= 0.0) { res.SunDown = true; res.R = 8192.0; return res; }
        RingTerrainShadowGeometry.RingPoint receiver = RingTerrainShadowGeometry.ReceiverPosition(Radius, a0, c0, t0);
        double a0Wrapped = a0 - Math.Floor(a0 / Circumference) * Circumference;
        double k = frame.Up * frame.Up + frame.Around * frame.Around, rho0 = Radius - t0, dPeak = rho0 * frame.Up / k;
        double earlyMax = Math.Max(b.OutsideMax, Math.Max(b.LevelMax[0], b.LevelMax[1]));
        double best = double.NegativeInfinity, dBest = 0.0, bestWx = 0.0, bestWy = 0.0;
        int bestLev = lev;
        double d = t / 8.0;
        int guard = 0;
        while (guard++ < 400_000)
        {
            RingTerrainShadowGeometry.RingRaySample ray = RingTerrainShadowGeometry.SampleStraightRay(Radius, receiver, sun, d);
            if (ray.AcrossMeters < 0.0 || ray.AcrossMeters > Width) break;
            double rise = ray.HeightMeters - t0;
            if (best > double.MinValue / 2 && d < dPeak && rise >= earlyMax - best) break;
            double da = ray.AroundMeters - a0Wrapped;
            da -= Circumference * Math.Round(da / Circumference);
            double a = a0 + da;
            int use = -1;
            double wx = 0, wy = 0;
            for (int l2 = lev; l2 < 2; l2++)
            {
                wx = a / LevelTexel[l2] - b.Win[l2].I0;
                wy = ray.AcrossMeters / LevelTexel[l2] - b.Win[l2].J0;
                if (InsideWindow(wx, wy)) { use = l2; break; }
            }
            if (use < 0)
            {
                if (b.OutsideMax - rise > best) res.Unresolved = true;
                break;
            }
            double v = SampleBilinear(b, use, wx, wy) - rise;
            if (v > best) { best = v; dBest = d; bestLev = use; bestWx = wx; bestWy = wy; }
            d += LevelTexel[use] / 8.0;
        }
        if (guard >= 400_000) res.Unresolved = true;
        if (best > double.MinValue / 2)
        {
            double h = 0.5;
            double gx = (SampleBilinear(b, bestLev, Math.Min(bestWx + h, N - 1), bestWy) - SampleBilinear(b, bestLev, Math.Max(bestWx - h, 0), bestWy)) / (LevelTexel[bestLev] * h * 2);
            double gy = (SampleBilinear(b, bestLev, bestWx, Math.Min(bestWy + h, N - 1)) - SampleBilinear(b, bestLev, bestWx, Math.Max(bestWy - h, 0))) / (LevelTexel[bestLev] * h * 2);
            res.SlopeTol = Math.Sqrt(gx * gx + gy * gy) * 0.5 * LevelTexel[bestLev];
        }
        if (f.Walls)
        {
            double sc = sun.Y;
            double dw = sc > 1e-6 ? (Width - c0) / sc : (sc < -1e-6 ? -c0 / sc : double.PositiveInfinity);
            if (dw > 1.0 && !double.IsInfinity(dw))
            {
                double wallH = f.WallTop - (RingTerrainShadowGeometry.SampleStraightRay(Radius, receiver, sun, dw).HeightMeters - t0);
                bool hit = RingTerrainShadowGeometry.RayHitsStructuralWall(Radius, RingTerrainShadowGeometry.ReceiverPosition(Radius, a0, c0, t0 + 0.5), sun, Width, -1e6, f.WallTop, 1e9, 1.0);
                if (hit != (wallH - t0 > 0.5) && Math.Abs(wallH - t0 - 0.5) > 0.05) res.WallMismatch = true;
                if (wallH > best) { best = wallH; dBest = dw; }
            }
        }
        res.R = Math.Clamp(best - t0, -2048.0, 8192.0);
        res.DBest = dBest;
        return res;
    }

    private static double HalfUlp(double value)
    {
        double x = Math.Max(Math.Abs(value), 1.0 / 16384.0);
        return Math.ScaleB(1.0, (int)Math.Floor(Math.Log2(x)) - 10);
    }

    // ---- comparison ----

    private sealed class Tally
    {
        public int Texels, SunDown, Shadowed, RefUnresolved, GpuUnresolved, WallMismatch;
        public int UnresAgree, UnresTotal, LsAgree, LsTotal, MagOk, MagOkSlope, MagTotal, GAgree, GTotal;
        public double Fraction(int ok, int total) => total == 0 ? 1.0 : (double)ok / total;
        public void Add(Tally o)
        {
            Texels += o.Texels; SunDown += o.SunDown; Shadowed += o.Shadowed; RefUnresolved += o.RefUnresolved; GpuUnresolved += o.GpuUnresolved; WallMismatch += o.WallMismatch;
            UnresAgree += o.UnresAgree; UnresTotal += o.UnresTotal; LsAgree += o.LsAgree; LsTotal += o.LsTotal; MagOk += o.MagOk; MagOkSlope += o.MagOkSlope; MagTotal += o.MagTotal;
            GAgree += o.GAgree; GTotal += o.GTotal;
        }
    }

    private Tally Compare(Built b, Fixture f, Vector3 sun, (float[] R, float[] G)[] gpuOut, int seed)
    {
        var picks = new List<(int Lev, int Ox, int Oy)>();
        var rng = new Random(seed);
        for (int lev = 0; lev < 2; lev++)
        {
            for (int i = 0; i < 4096; i++) picks.Add((lev, rng.Next(N), rng.Next(N)));
            foreach (int row in new[] { 512, 777 })
                for (int ox = 0; ox < N; ox++) picks.Add((lev, ox, row));
        }
        var refs = new RefResult[picks.Count];
        Parallel.For(0, picks.Count, i => refs[i] = Trace(b, f, sun, picks[i].Lev, picks[i].Ox, picks[i].Oy));
        var tally = new Tally();
        for (int i = 0; i < picks.Count; i++)
        {
            (int lev, int ox, int oy) = picks[i];
            HeightShadowWindow win = b.Win[lev];
            int slot = RingHeightShadowLattice.SlotOf(win.J0 + oy) * N + RingHeightShadowLattice.SlotOf(win.I0 + ox);
            double gpuR = gpuOut[lev].R[slot], gpuG = gpuOut[lev].G[slot];
            RefResult r = refs[i];
            tally.Texels++;
            if (r.SunDown) { tally.SunDown++; continue; }
            if (r.WallMismatch) tally.WallMismatch++;
            bool gpuUnres = gpuG < 0.0, refUnres = r.Unresolved;
            if (gpuUnres) tally.GpuUnresolved++;
            if (refUnres) tally.RefUnresolved++;
            tally.UnresTotal++;
            if (gpuUnres == refUnres) tally.UnresAgree++;
            if (gpuUnres || refUnres) continue;
            if (r.R > 0.5) tally.Shadowed++;
            double penumbra = Math.Max(0.25, r.DBest * Math.Tan(0.25 * Math.PI / 180.0)) + r.SlopeTol;
            if (Math.Abs(r.R - 0.5) >= penumbra)
            {
                tally.LsTotal++;
                if ((gpuR > 0.5) == (r.R > 0.5)) tally.LsAgree++;
            }
            if (r.R > 0.0)
            {
                double tolerance = Math.Max(0.5, 0.01 * r.DBest) + HalfUlp(r.R);
                tally.MagTotal++;
                if (Math.Abs(gpuR - r.R) <= tolerance) tally.MagOk++;
                if (Math.Abs(gpuR - r.R) <= tolerance + r.SlopeTol) tally.MagOkSlope++;
                if (r.R > 0.5 && gpuR > 0.5)
                {
                    tally.GTotal++;
                    if (Math.Abs(gpuG - r.DBest) <= Math.Max(0.5, 0.01 * r.DBest) + r.SlopeTol + 0.5 * LevelTexel[lev]) tally.GAgree++;
                }
            }
        }
        return tally;
    }

    private void ReportAndGate(string label, Tally t)
    {
        GD.Print($"HEIGHT_SHADOW_REF {label} n={t.Texels} sunDown={t.SunDown} shadowed={t.Shadowed} refUnres={t.RefUnresolved} gpuUnres={t.GpuUnresolved} " +
                 $"litShadow={t.Fraction(t.LsAgree, t.LsTotal):P2} (n={t.LsTotal}) magnitude={t.Fraction(t.MagOk, t.MagTotal):P2} (+slope {t.Fraction(t.MagOkSlope, t.MagTotal):P2}, n={t.MagTotal}) " +
                 $"unresolvedFlags={t.Fraction(t.UnresAgree, t.UnresTotal):P2} blockerDist={t.Fraction(t.GAgree, t.GTotal):P2} (n={t.GTotal}) wallFormulaMismatch={t.WallMismatch}");
        Gate(t.LsTotal < 100 || t.Fraction(t.LsAgree, t.LsTotal) >= 0.99, $"{label}: lit/shadow agreement {t.Fraction(t.LsAgree, t.LsTotal):P2}");
        Gate(t.MagTotal < 100 || t.Fraction(t.MagOkSlope, t.MagTotal) >= 0.99, $"{label}: shadow-line magnitude within max(0.5 m, 1% d) + half-texel slope allowance {t.Fraction(t.MagOkSlope, t.MagTotal):P2} (plain {t.Fraction(t.MagOk, t.MagTotal):P2})");
        Gate(t.UnresTotal < 100 || t.Fraction(t.UnresAgree, t.UnresTotal) >= 0.99, $"{label}: unresolved flag agreement {t.Fraction(t.UnresAgree, t.UnresTotal):P2}");
    }

    private void Gate(bool ok, string message)
    {
        _checks++;
        if (!ok) { _failures.Add(message); GD.PushError("HEIGHT_SHADOW_REF gate failed: " + message); }
    }

    // ---- unwindowed truth for the beyond-L1 fixtures ----

    private struct TruthResult
    {
        public double RFull, DFull, RNear;
        public bool SunDown;
    }

    /// <summary>Double march on the analytic profile with no window limit (60 km); RNear only counts samples inside the L1 window (what the build can see).</summary>
    private static TruthResult TruthTrace(Built b, Fixture f, Vector3 sun, int lev, int ox, int oy)
    {
        HeightShadowWindow win = b.Win[lev];
        int t = LevelTexel[lev];
        long iIndex = win.I0 + ox, jIndex = win.J0 + oy;
        double t0 = b.H[lev][RingHeightShadowLattice.SlotOf(jIndex) * N + RingHeightShadowLattice.SlotOf(iIndex)];
        double a0 = iIndex * (double)t, c0 = jIndex * (double)t;
        var res = new TruthResult();
        HeightShadowSunFrame frame = RingHeightShadowLattice.ProjectToTangentFrame(sun.X, sun.Y, sun.Z, a0 / Radius);
        if (frame.Up <= 0.0) { res.SunDown = true; return res; }
        RingTerrainShadowGeometry.RingPoint receiver = RingTerrainShadowGeometry.ReceiverPosition(Radius, a0, c0, t0);
        double a0Wrapped = a0 - Math.Floor(a0 / Circumference) * Circumference;
        double bestFull = double.NegativeInfinity, bestNear = double.NegativeInfinity, dFull = 0.0;
        double outsideMax = b.OutsideMax;
        for (double d = 1.0; d < 60_000.0; d += d < 16_000.0 ? 3.0 : 20.0)
        {
            RingTerrainShadowGeometry.RingRaySample ray = RingTerrainShadowGeometry.SampleStraightRay(Radius, receiver, sun, d);
            if (ray.AcrossMeters < 0.0 || ray.AcrossMeters > Width) break;
            double rise = ray.HeightMeters - t0;
            if (bestFull > double.MinValue / 2 && rise >= outsideMax - bestFull) break;
            double da = ray.AroundMeters - a0Wrapped;
            da -= Circumference * Math.Round(da / Circumference);
            double a = a0 + da;
            double v = f.Profile!(a - f.CamAround, ray.AcrossMeters - f.CamAcross) - rise;
            if (v > bestFull) { bestFull = v; dFull = d; }
            double wx = a / LevelTexel[1] - b.Win[1].I0, wy = ray.AcrossMeters / LevelTexel[1] - b.Win[1].J0;
            if (InsideWindow(wx, wy) && v > bestNear) bestNear = v;
        }
        res.RFull = bestFull - t0; res.DFull = dFull; res.RNear = bestNear - t0;
        return res;
    }

    private void RunBeyondFixture(RingTerrainHeightShadowGpu gpu, RenderingDevice rd, Fixture f)
    {
        var watch = Stopwatch.StartNew();
        Built b = BuildLevels(gpu, f);
        b.OutsideMax = 3200f; // tight bound for the massif, like the S4c pyramid bound at the fixture
        GD.Print($"HEIGHT_SHADOW_REF fixture={f.Name} setup={watch.Elapsed.TotalSeconds:F1}s L0max={b.LevelMax[0]:F0} L1max={b.LevelMax[1]:F0} outsideMax={b.OutsideMax:F0}");
        int seed = 500;
        foreach ((double elev, double az) in f.Suns)
        {
            Vector3 sun = SunVector(f.CamAround, elev, az);
            var gpuOut = RunBuild(gpu, rd, f, b, sun);
            watch.Restart();
            var picks = new List<(int Lev, int Ox, int Oy)>();
            var rng = new Random(seed++);
            for (int lev = 0; lev < 2; lev++)
            {
                for (int i = 0; i < 3072; i++) picks.Add((lev, rng.Next(N), rng.Next(N)));
                foreach (int row in new[] { 512, 777 })
                    for (int ox = 0; ox < N; ox += 2) picks.Add((lev, ox, row));
            }
            var windowed = new RefResult[picks.Count];
            var truth = new TruthResult[picks.Count];
            Parallel.For(0, picks.Count, i =>
            {
                windowed[i] = Trace(b, f, sun, picks[i].Lev, picks[i].Ox, picks[i].Oy);
                truth[i] = TruthTrace(b, f, sun, picks[i].Lev, picks[i].Ox, picks[i].Oy);
            });
            int texels = 0, unres = 0, unresShadowed = 0, litSoFarTruthShadowed = 0;
            int magTotal = 0, magOk = 0, lsTotal = 0, lsOk = 0, gTotal = 0, gOk = 0, massifTotal = 0, massifUnres = 0, ridgeTotal = 0, ridgeOk = 0, resolved = 0, wrongLit = 0;
            for (int i = 0; i < picks.Count; i++)
            {
                (int lev, int ox, int oy) = picks[i];
                HeightShadowWindow win = b.Win[lev];
                int slot = RingHeightShadowLattice.SlotOf(win.J0 + oy) * N + RingHeightShadowLattice.SlotOf(win.I0 + ox);
                double gpuR = gpuOut[lev].R[slot], gpuG = gpuOut[lev].G[slot];
                RefResult w = windowed[i];
                TruthResult tr = truth[i];
                if (w.SunDown || tr.SunDown) continue;
                texels++;
                double slopeTol = w.SlopeTol;
                double penWindowed = Math.Max(0.25, w.DBest * Math.Tan(0.25 * Math.PI / 180.0)) + slopeTol;
                double penTruth = Math.Max(0.25, tr.DFull * Math.Tan(0.25 * Math.PI / 180.0)) + slopeTol;
                bool truthShadowed = tr.RFull - 0.5 >= penTruth;
                if (gpuG < 0.0)
                {
                    unres++;
                    if (gpuR > 0.5) unresShadowed++;
                    else if (truthShadowed) litSoFarTruthShadowed++;
                    // R of an unresolved texel is the windowed reference's lower bound.
                    if (w.R > 0.0)
                    {
                        magTotal++;
                        if (Math.Abs(gpuR - w.R) <= Math.Max(0.5, 0.01 * w.DBest) + HalfUlp(w.R) + slopeTol) magOk++;
                        if (w.R > 0.5 && gpuR > 0.5)
                        {
                            gTotal++;
                            if (Math.Abs(-gpuG - w.DBest) <= Math.Max(0.5, 0.01 * w.DBest) + slopeTol + 0.5 * LevelTexel[lev]) gOk++;
                        }
                    }
                    if (Math.Abs(w.R - 0.5) >= penWindowed)
                    {
                        lsTotal++;
                        if ((gpuR > 0.5) == (w.R > 0.5)) lsOk++;
                    }
                    if (Math.Abs(tr.RNear - 0.5) >= penWindowed)
                    {
                        ridgeTotal++;
                        if ((gpuR > 0.5) == (tr.RNear > 0.5)) ridgeOk++;
                    }
                }
                else
                {
                    resolved++;
                    if (truthShadowed && gpuR <= 0.5) wrongLit++;
                }
                if (truthShadowed && tr.DFull > 9000.0)
                {
                    massifTotal++;
                    if (gpuG < 0.0) massifUnres++;
                }
            }
            string label = $"fixture={f.Name} sun=({elev:0.#} deg elev, {az:0.#} deg az)";
            GD.Print($"HEIGHT_SHADOW_REF {label} n={texels} unresolved={unres} (shadowed so far {unresShadowed}, lit so far {unres - unresShadowed}, of which truth-shadowed {litSoFarTruthShadowed}) resolved={resolved} wrongLitResolved={wrongLit} " +
                     $"G<0 magnitude={Frac(magOk, magTotal):P2} (n={magTotal}) G<0 lit/shadow vs windowed={Frac(lsOk, lsTotal):P2} (n={lsTotal}) |G|={Frac(gOk, gTotal):P2} (n={gTotal}) " +
                     $"massifZoneUnresolved={Frac(massifUnres, massifTotal):P3} (n={massifTotal}) ridgeInUnknown vs truth={Frac(ridgeOk, ridgeTotal):P2} (n={ridgeTotal}) ({watch.Elapsed.TotalSeconds:F1} s)");
            Gate(magTotal < 100 || Frac(magOk, magTotal) >= 0.99, $"{label}: G<0 shadow-line magnitude vs the windowed reference {Frac(magOk, magTotal):P2}");
            Gate(lsTotal < 100 || Frac(lsOk, lsTotal) >= 0.99, $"{label}: G<0 lit/shadow vs the windowed reference {Frac(lsOk, lsTotal):P2}");
            Gate(gTotal < 100 || Frac(gOk, gTotal) >= 0.99, $"{label}: G<0 blocker distance (|G|) vs the windowed reference {Frac(gOk, gTotal):P2}");
            Gate(massifTotal < 100 || Frac(massifUnres, massifTotal) >= 0.999, $"{label}: massif shadow zone resolved-lit ({Frac(massifUnres, massifTotal):P3} unresolved)");
            Gate(ridgeTotal < 100 || Frac(ridgeOk, ridgeTotal) >= 0.99, $"{label}: ridge shadow inside the unknown zone vs truth {Frac(ridgeOk, ridgeTotal):P2}");
            Gate(resolved < 100 || Frac(wrongLit, resolved) <= 0.001, $"{label}: resolved (G >= 0) texels lit where truth is shadowed {Frac(wrongLit, resolved):P3}");
            Gate(texels == 0 || Frac(wrongLit, texels) <= 0.005, $"{label}: mis-lit {Frac(wrongLit, texels):P3} of all texels");
        }
    }

    private static double Frac(int ok, int total) => total == 0 ? 1.0 : (double)ok / total;

    // ---- fixtures run ----

    private void RunFixture(RingTerrainHeightShadowGpu gpu, RenderingDevice rd, Fixture f)
    {
        var watch = Stopwatch.StartNew();
        Built b = BuildLevels(gpu, f);
        GD.Print($"HEIGHT_SHADOW_REF fixture={f.Name} setup={watch.Elapsed.TotalSeconds:F1}s L0max={b.LevelMax[0]:F0} L1max={b.LevelMax[1]:F0} outsideMax={b.OutsideMax:F0}");
        int seed = 100;
        foreach ((double elev, double az) in f.Suns)
        {
            Vector3 sun = SunVector(f.CamAround, elev, az);
            watch.Restart();
            var gpuOut = RunBuild(gpu, rd, f, b, sun);
            double gpuSeconds = watch.Elapsed.TotalSeconds;
            watch.Restart();
            Tally tally = Compare(b, f, sun, gpuOut, seed++);
            ReportAndGate($"fixture={f.Name} sun=({elev:0.#} deg elev, {az:0.#} deg az)", tally);
            GD.Print($"HEIGHT_SHADOW_REF   (gpu build+readback {gpuSeconds * 1000:F0} ms, reference {watch.Elapsed.TotalSeconds:F1} s)");
        }
    }

    private Fixture? MountainFixture(out List<IHeightShadowSource> owned)
    {
        owned = new List<IHeightShadowSource>();
        RingTerrainSampler root = EmitterSiteTestWorlds.CreateGen17Landform(20260419);
        IHeightShadowSource probe = RingTerrainHeightShadowSource.Wrap(root.CreateIndependentCopy());
        long bestAround = 0; int bestAcross = 0; float bestHeight = float.MinValue;
        for (long a = 2_000_000; a < 4_000_000; a += 512)
            for (int c = 30_000; c < 150_000; c += 512)
            {
                float h = probe.GetHeight(a, c);
                if (h > bestHeight) { bestHeight = h; bestAround = a; bestAcross = c; }
            }
        probe.Dispose();
        GD.Print($"HEIGHT_SHADOW_REF real mountain site: around={bestAround} across={bestAcross} height={bestHeight:F0} m (Gen-17 landform v7 seed 20260419)");
        for (int i = 0; i < 2; i++) owned.Add(RingTerrainHeightShadowSource.Wrap(root.CreateIndependentCopy()));
        long cam = (bestAround - 1500) / 64 * 64;
        int camAcross = (int)(bestAcross / 64 * 64);
        return new Fixture
        {
            Name = "real_mountain", CamAround = cam, CamAcross = camAcross, Sources = owned.ToArray(), Height = (_, _) => 0f,
            Suns = new[] { (10.0, 0.0), (4.0, 25.0), (1.5, 340.0), (25.0, 0.0), (6.0, 180.0) },
        };
    }

    // ---- low sun tips ----

    private static double Smooth(double x) { x = Math.Clamp(x, 0.0, 1.0); return x * x * (3.0 - 2.0 * x); }

    /// <summary>Plateau profile along the sun axis: smooth leading edge over [300, 400] m, flat top, smooth trailing edge over [600, 700] m (a hard step makes the tip depend on sub-sample phase).</summary>
    private static double TipProfile(double coordinate, double plateau) => plateau * (Smooth((coordinate - 300.0) / 100.0) - Smooth((coordinate - 600.0) / 100.0));

    private void RunTipTest(RingTerrainHeightShadowGpu gpu, RenderingDevice rd, bool acrossAxis, double elevDeg)
    {
        const long cam = 3_000_000;
        const int camAcross = 100_000;
        const float ground = 100f;
        float plateau = (float)(1600.0 * Math.Tan(elevDeg * Math.PI / 180.0));
        var f = new Fixture
        {
            Name = "tip", CamAround = cam, CamAcross = camAcross,
            Height = (a, c) => ground + (float)TipProfile(acrossAxis ? c - camAcross : SignedAround(a, cam), plateau),
        };
        Built b = BuildLevels(gpu, f);
        b.OutsideMax = ground + plateau + 5f; // flat outside the window: tight bound, nothing is flagged unresolved by the 8 km exit
        Vector3 sun = SunVector(cam, elevDeg, acrossAxis ? 90.0 : 0.0);
        var gpuOut = RunBuild(gpu, rd, f, b, sun);
        var refs = new RefResult[N];
        Parallel.For(0, N, k => refs[k] = acrossAxis ? Trace(b, f, sun, 0, 512, k) : Trace(b, f, sun, 0, k, 512));
        HeightShadowWindow win = b.Win[0];
        double Coordinate(int k) => acrossAxis ? (win.J0 + k) * 4.0 - camAcross : (win.I0 + k) * 4.0 - cam;
        int edge = Enumerable.Range(0, N).First(k => Coordinate(k) == 296.0); // last ground texel before the leading edge
        double[] gpuR = new double[N];
        for (int k = 0; k < N; k++)
            gpuR[k] = acrossAxis ? gpuOut[0].R[RingHeightShadowLattice.SlotOf(win.J0 + k) * N + RingHeightShadowLattice.SlotOf(win.I0 + 512)]
                                 : gpuOut[0].R[RingHeightShadowLattice.SlotOf(win.J0 + 512) * N + RingHeightShadowLattice.SlotOf(win.I0 + k)];
        int Tip(Func<int, bool> shadowed)
        {
            if (!shadowed(edge)) return -1;
            int k = edge;
            while (k > 0 && shadowed(k - 1)) k--;
            return k;
        }
        if (_only == "tipdbg") foreach (double xs in new[] { -1288.0, -1000.0, -400.0, 0.0, 200.0, 280.0 })
        {
            int kk = Enumerable.Range(0, N).First(k => Coordinate(k) >= xs);
            int sl = acrossAxis ? RingHeightShadowLattice.SlotOf(win.J0 + kk) * N + RingHeightShadowLattice.SlotOf(win.I0 + 512) : RingHeightShadowLattice.SlotOf(win.J0 + 512) * N + RingHeightShadowLattice.SlotOf(win.I0 + kk);
            GD.Print($"DBG x={Coordinate(kk)} gpuR={gpuOut[0].R[sl]} gpuG={gpuOut[0].G[sl]} refR={refs[kk].R} refD={refs[kk].DBest}");
        }
        int tipGpu = Tip(k => gpuR[k] > 0.5), tipRef = Tip(k => refs[k].R > 0.5);
        double tipFlat = ExactTip(sun, cam, camAcross, ground, plateau, acrossAxis, flat: true);
        double tipExact = ExactTip(sun, cam, camAcross, ground, plateau, acrossAxis, flat: false);
        string axis = acrossAxis ? "across" : "around";
        Gate(tipGpu >= 0 && tipRef >= 0, $"tip {axis} {elevDeg} deg: no shadow run reaches the plateau edge (gpu {tipGpu}, ref {tipRef})");
        if (tipGpu < 0 || tipRef < 0) return;
        double gpuTip = Coordinate(tipGpu), refTip = Coordinate(tipRef);
        double extensionExact = tipFlat - tipExact, extensionGpu = tipFlat - gpuTip;
        GD.Print($"HEIGHT_SHADOW_TIP axis={axis} elev={elevDeg} deg plateau={plateau:F1} m tip gpu={gpuTip:F0} ref={refTip:F0} exact={tipExact:F1} flat-earth={tipFlat:F1} " +
                 $"shadow longer than flat-earth by gpu={extensionGpu:F1} m exact={extensionExact:F1} m");
        Gate(Math.Abs(gpuTip - refTip) <= 4.0, $"tip {axis} {elevDeg} deg: gpu tip {gpuTip} vs reference {refTip} differ by more than one texel");
        Gate(Math.Abs(gpuTip - tipExact) <= 4.0, $"tip {axis} {elevDeg} deg: gpu tip {gpuTip} vs exact {tipExact:F1} differ by more than one texel");
        if (Math.Abs(extensionExact) >= 6.0) Gate(Math.Sign(extensionGpu) == Math.Sign(extensionExact), $"tip {axis} {elevDeg} deg: curvature delta has the wrong sign (gpu {extensionGpu:F1}, exact {extensionExact:F1})");
        else Gate(Math.Abs(extensionGpu) <= 6.0, $"tip {axis} {elevDeg} deg: curvature delta {extensionGpu:F1} m should be ~0");
        if (acrossAxis) Gate(Math.Abs(extensionExact) < 1.0, $"tip across: exact geometry should have no curvature delta ({extensionExact:F2} m)");
    }

    /// <summary>
    /// Exact (double, 0.2 m ray steps on the analytic profile, no lattice) shadow tip coordinate for a ground receiver: the smallest coordinate whose ray still
    /// dips below the plateau profile. <paramref name="flat"/> uses a straight flat-earth ray instead of the curved ring ray.
    /// </summary>
    private static double ExactTip(Vector3 sun, long cam, int camAcross, double ground, double plateau, bool acrossAxis, bool flat)
    {
        bool Blocked(double x)
        {
            double a = cam + x;
            var receiver = RingTerrainShadowGeometry.ReceiverPosition(Radius, acrossAxis ? cam : a, acrossAxis ? camAcross + x : camAcross, ground);
            double aWrapped = (acrossAxis ? cam : a) - Math.Floor((acrossAxis ? cam : a) / Circumference) * Circumference;
            double horizontal = flat ? Math.Sqrt(Math.Max(0.0, 1.0 - SunUpAtCamera(sun, cam) * SunUpAtCamera(sun, cam))) : 1.0;
            for (double d = 0.2; d < 40_000.0; d += 0.2)
            {
                double coordinate, rise;
                if (flat)
                {
                    rise = d * SunUpAtCamera(sun, cam);
                    coordinate = x + d * horizontal;
                }
                else
                {
                    RingTerrainShadowGeometry.RingRaySample ray = RingTerrainShadowGeometry.SampleStraightRay(Radius, receiver, sun, d);
                    rise = ray.HeightMeters - ground;
                    if (acrossAxis) coordinate = ray.AcrossMeters - camAcross;
                    else
                    {
                        double delta = ray.AroundMeters - aWrapped;
                        delta -= Circumference * Math.Round(delta / Circumference);
                        coordinate = x + delta;
                    }
                }
                if (coordinate > 700.0) return false;
                if (TipProfile(coordinate, plateau) - rise > 0.5) return true; // receiver 0.5 m above the column surface, like the lit/shadow test
            }
            return false;
        }
        double low = -6000.0, high = 296.0;
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (low + high);
            if (Blocked(mid)) high = mid; else low = mid;
        }
        return 0.5 * (low + high);
    }

    private static double SunUpAtCamera(Vector3 sun, long cam) => RingHeightShadowLattice.ProjectToTangentFrame(sun.X, sun.Y, sun.Z, cam / Radius).Up;

    // ---- seam ----

    private void RunSeamTest(RingTerrainHeightShadowGpu gpu, RenderingDevice rd)
    {
        long shiftIndexless = 16384L * 1900L; // multiple of the anchor period P = N * 16 m: identical anchor-local coordinates and slot layout
        long camA = Circumference - 704, camB = camA - shiftIndexless;
        camB = ((camB % Circumference) + Circumference) % Circumference;
        Fixture fa = RidgeFixture("seam_a", camA), fb = RidgeFixture("seam_b", camB);
        Built ba = BuildLevels(gpu, fa);
        Gate(ba.Win[0].I1 * 4L > Circumference && ba.Win[0].I0 * 4L < Circumference, "seam fixture window must straddle the seam");
        float[][] heightsA = { (float[])ba.H[0].Clone(), (float[])ba.H[1].Clone() };
        Vector3 sunA = SunVector(camA, 6.0, 20.0);
        var outA = RunBuild(gpu, rd, fa, ba, sunA);
        outA = new[] { (outA[0].R, outA[0].G), (outA[1].R, outA[1].G) };
        Built bb = BuildLevels(gpu, fb);
        Vector3 sunB = SunVector(camB, 6.0, 20.0);
        var outB = RunBuild(gpu, rd, fb, bb, sunB);
        for (int l = 0; l < 2; l++)
            Gate(heightsA[l].AsSpan().SequenceEqual(bb.H[l]), $"seam fixture: level {l} heights differ from the shifted fixture");
        int bad = 0, total = 0;
        double maxDr = 0, maxDg = 0;
        for (int l = 0; l < 2; l++)
            for (int i = 0; i < outA[l].R.Length; i++)
            {
                total++;
                double dr = Math.Abs(outA[l].R[i] - outB[l].R[i]), dg = Math.Abs(outA[l].G[i] - outB[l].G[i]);
                maxDr = Math.Max(maxDr, dr); maxDg = Math.Max(maxDg, dg);
                if (dr > HalfUlp(outA[l].R[i]) * 2 + 0.02 || dg > Math.Abs(outA[l].G[i]) * 0.002 + 0.1) { bad++; }
            }
        GD.Print($"HEIGHT_SHADOW_SEAM texels={total} mismatching={bad} maxDeltaR={maxDr:F4} maxDeltaG={maxDg:F4} (camera {camA} vs {camB}, shift {camA - camB} m)");
        Gate(bad <= total / 10_000, $"seam fixture differs from the shifted fixture in {bad} of {total} texels");
    }

    // ---- timing ----

    private void RunTiming(RingTerrainHeightShadowGpu gpu, RenderingDevice rd)
    {
        Fixture f = RidgeFixture("timing", 3_000_000);
        Built b = BuildLevels(gpu, f);
        foreach ((string label, double elev, double az) in new[] { ("noon 80 deg", 80.0, 0.0), ("noon 80 deg + ridge in view (az 0)", 80.0, 15.0), ("low sun 3 deg", 3.0, 0.0), ("low sun 3 deg across", 3.0, 90.0) })
        {
            gpu.SetBuildInputs(MakeInputs(f, b, SunVector(f.CamAround, elev, az)));
            for (int level = 0; level < 2; level++)
            {
                for (int warm = 0; warm < 20; warm++) gpu.DispatchBuild(level);
                rd.Submit();
                rd.Sync();
                var wall = new List<double>();
                var gpuTime = new List<double>();
                for (int rep = 0; rep < 10; rep++)
                {
                    var watch = Stopwatch.StartNew();
                    rd.CaptureTimestamp("hs_begin");
                    gpu.DispatchBuild(level);
                    rd.CaptureTimestamp("hs_end");
                    rd.Submit();
                    rd.Sync();
                    wall.Add(watch.Elapsed.TotalMilliseconds);
                    uint count = rd.GetCapturedTimestampsCount();
                    if (count >= 2) gpuTime.Add((rd.GetCapturedTimestampGpuTime(count - 1) - rd.GetCapturedTimestampGpuTime(count - 2)) / 1e6);
                }
                wall.Sort();
                gpuTime.Sort();
                string gpuText = gpuTime.Count > 0 ? $"{gpuTime[gpuTime.Count / 2]:F2} ms (min {gpuTime[0]:F2}, max {gpuTime[^1]:F2})" : "unavailable";
                GD.Print($"HEIGHT_SHADOW_BUILD_TIME {label} level={level} whole-level dispatch: gpu timestamps median={gpuText}; submit+sync wall median={wall[wall.Count / 2]:F2} ms (non-authoritative, RTX 4070 desktop)");
            }
        }
    }

    // ---- entry ----

    public bool Run()
    {
        try
        {
            foreach (string arg in OS.GetCmdlineUserArgs())
                if (arg.StartsWith("--only=")) _only = arg.Substring(7);
            RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice() ?? throw new InvalidOperationException("no local RenderingDevice (run windowed, not --headless)");
            using var gpu = new RingTerrainHeightShadowGpu(rd);
            if (!gpu.Initialize()) throw new InvalidOperationException("GPU init failed: " + gpu.Error);
            bool Wanted(string name) => _only.Length == 0 || name.Contains(_only, StringComparison.OrdinalIgnoreCase);

            foreach (Fixture f in SyntheticFixtures())
                if (Wanted(f.Name)) RunFixture(gpu, rd, f);
            foreach (Fixture f in BeyondFixtures())
                if (Wanted(f.Name)) RunBeyondFixture(gpu, rd, f);
            if (Wanted("real_mountain"))
            {
                Fixture? mountain = MountainFixture(out List<IHeightShadowSource> owned);
                try { if (mountain != null) RunFixture(gpu, rd, mountain); }
                finally { foreach (IHeightShadowSource s in owned) s.Dispose(); }
            }
            if (Wanted("tip") || _only == "tipdbg")
                foreach (double elev in new[] { 0.5, 1.0, 3.0 })
                    foreach (bool across in new[] { false, true })
                        RunTipTest(gpu, rd, across, elev);
            if (Wanted("seam")) RunSeamTest(gpu, rd);
            if (Wanted("timing")) RunTiming(gpu, rd);

            if (_failures.Count > 0)
            {
                GD.PushError($"HEIGHT_SHADOW_REFERENCE=FAIL gates={_checks} failures={_failures.Count}");
                return false;
            }
            GD.Print($"HEIGHT_SHADOW_REFERENCE=PASS gates={_checks}");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("HEIGHT_SHADOW_REFERENCE=FAIL " + exception.Message + "\n" + exception.StackTrace);
            return false;
        }
    }
}
