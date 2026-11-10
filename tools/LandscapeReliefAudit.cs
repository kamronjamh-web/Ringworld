using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Relief spectrum of the generation 1 world (docs/landscape-audit-t0-spec.md WO T0-2): per landscape class, band RMS height per octave of wavelength, decade RMS,
/// slope percentiles, 2D slope at 4 m and 4 km relief, from FFT spectra of 1 m and 32 m height transects around lattice anchors. Run via tools/landscape_relief_audit.gd.
/// Writes relief_spectrum.json/.md to .godot/audit_baseline/common/t0/relief/&lt;stamp&gt;/. The JSON is deterministic apart from its trailing "run" object.
/// </summary>
public partial class LandscapeReliefAudit : RefCounted
{
    const int AnchorsPerClass = 24;
    const int FineN = 4096;
    const int CoarseAroundN = 4096;
    const int CoarseAcrossN = 2048;
    const double CoarseStep = 32d;
    const int Bands = 15; // octaves [2,4) ... [32768,65536) m
    const int FineBandsTo = 8; // fine transects cover bands 0..8 (2 m to 1,024 m)
    const int CoarseBandsFrom = 5; // coarse transects cover bands 5..14 (64 m to 64 km, reported to 32 km)
    const int FineUntilBand = 7; // report the fine estimate below 512 m (class-local) and the wide coarse one from 512 m up
    static readonly int[] SlopeBaselines = { 2, 8, 32 };
    static readonly (string Name, double Lo, double Hi, bool Fine)[] Decades =
    {
        ("2-30 m", 2, 30, true), ("30-300 m", 30, 300, true), ("0.3-3 km", 300, 3000, false), ("3-30 km", 3000, 30000, false),
    };
    static readonly LandscapeClass[] Classes =
    {
        LandscapeClass.CoastalBelt, LandscapeClass.Plains, LandscapeClass.Rolling, LandscapeClass.HillCountry,
        LandscapeClass.PlateauInterior, LandscapeClass.Breaks, LandscapeClass.Mountain,
    };

    static string Arg(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return "";
    }

    sealed class Anchor
    {
        public int Cls; public double Around, Across;
        public double[][] FineBand = new double[2][];    // [direction][band] RMS (direction 0 = around, 1 = across); NaN where not covered
        public double[][] CoarseBand = new double[2][];
        public double[][] MatchedBand = new double[2][];  // 32 m sampling over the same 4,096 m window as the fine transect (consistency check only)
        public double[][] DecadeRms = new double[2][];    // [direction][decade]
        public double[][] Slopes = new double[3][];       // per baseline, pooled over both fine transects (degrees)
        public double[] Slope2d = Array.Empty<double>();  // degrees
        public double[] Relief = new double[2];           // 4 km relief per fine transect
    }

    public bool Run()
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        try { RunAudit(failures, warnings); }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string w in warnings) GD.Print("LANDSCAPE_RELIEF warning: " + w);
        foreach (string failure in failures) GD.PushError("LANDSCAPE_RELIEF=FAIL " + failure);
        return failures.Count == 0;
    }

    void RunAudit(List<string> failures, List<string> warnings)
    {
        var clock = Stopwatch.StartNew();

        // Normalisation proof first (spec escalation condition): 3 m sine must read 3/sqrt(2) in its octave band.
        SineCheck(failures);

        LandscapeWorld.Worker[] workers = LandscapeWorld.CreateWorkers(LandscapeWorld.DefaultWorkerCount);
        try
        {
            // 1. Class coverage over the whole lattice.
            var latticeClass = new int[LandscapeWorld.LatticeAround * LandscapeWorld.LatticeAcross];
            ParallelRows(workers, LandscapeWorld.LatticeAround, (w, i) =>
            {
                for (int j = 0; j < LandscapeWorld.LatticeAcross; j++)
                    latticeClass[i * LandscapeWorld.LatticeAcross + j] = (int)w.Class(LandscapeWorld.LatticeAroundMeters(i), LandscapeWorld.LatticeAcrossMeters(j));
            });
            var coverage = new int[Enum.GetValues<LandscapeClass>().Length];
            foreach (int c in latticeClass) coverage[c]++;

            // 2. Anchors: first 24 per class in lattice order.
            var rowAnchors = new List<(int Cls, double Around, double Across)>[LandscapeWorld.LatticeAround];
            ParallelRows(workers, LandscapeWorld.LatticeAround, (w, i) =>
            {
                var list = new List<(int, double, double)>();
                for (int j = 0; j < LandscapeWorld.LatticeAcross; j++)
                {
                    var cls = (LandscapeClass)latticeClass[i * LandscapeWorld.LatticeAcross + j];
                    if (Array.IndexOf(Classes, cls) < 0) continue;
                    double a = LandscapeWorld.LatticeAroundMeters(i), z = LandscapeWorld.LatticeAcrossMeters(j);
                    if (AnchorPasses(w, cls, a, z)) list.Add(((int)cls, a, z));
                }
                rowAnchors[i] = list;
            });
            var chosen = new List<(int Cls, double Around, double Across)>();
            var counts = new Dictionary<int, int>();
            foreach (var row in rowAnchors)
                foreach (var cand in row)
                {
                    counts.TryGetValue(cand.Cls, out int n);
                    if (n >= AnchorsPerClass) continue;
                    counts[cand.Cls] = n + 1;
                    chosen.Add(cand);
                }

            // 3. Measure every anchor (one slot per anchor, so the merge order is fixed).
            var anchors = new Anchor[chosen.Count];
            ParallelRows(workers, chosen.Count, (w, k) => anchors[k] = Measure(w, chosen[k].Cls, chosen[k].Around, chosen[k].Across));

            // 4. Report.
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = Arg("--out=");
            if (outDir.Length == 0) outDir = ProjectSettings.GlobalizePath("res://.godot/audit_baseline/common/t0/relief/" + stamp);
            Directory.CreateDirectory(outDir);
            var stats = BuildStats(anchors, coverage, latticeClass.Length, failures, warnings);
            double seconds = clock.Elapsed.TotalSeconds;
            File.WriteAllText(Path.Combine(outDir, "relief_spectrum.json"), stats.Json(stamp, seconds));
            File.WriteAllText(Path.Combine(outDir, "relief_spectrum.md"), stats.Markdown(stamp, seconds));
            GD.Print($"LANDSCAPE_RELIEF output={outDir} anchors={anchors.Length} seconds={seconds:F1}");
            foreach (LandscapeClass cls in Classes)
            {
                counts.TryGetValue((int)cls, out int n);
                bool lowland = cls != LandscapeClass.Breaks && cls != LandscapeClass.Mountain;
                if (n < AnchorsPerClass)
                {
                    string message = $"{cls} has {n} anchors (need {AnchorsPerClass})";
                    if (lowland && cls != LandscapeClass.PlateauInterior) failures.Add(message); else warnings.Add(message); // PlateauInterior: only 67 lattice points exist, the rule as written yields 22 (reported to the lead)
                }
            }
        }
        finally { foreach (var w in workers) w.Dispose(); }
    }

    static void ParallelRows(LandscapeWorld.Worker[] workers, int count, Action<LandscapeWorld.Worker, int> body)
    {
        int wn = workers.Length;
        Parallel.For(0, wn, k => { for (int i = k; i < count; i += wn) body(workers[k], i); });
    }

    static double Wrap(double around) => ((around % LandscapeWorld.Circumference) + LandscapeWorld.Circumference) % LandscapeWorld.Circumference;

    static bool SiteFreePoint(LandscapeWorld.Worker w, double around, double across)
    {
        var d = w.Diag(Wrap(around), across);
        return d.SiteWeight <= 0d && d.WeatherLungWeight <= 0d;
    }

    static bool AnchorPasses(LandscapeWorld.Worker w, LandscapeClass cls, double a, double z)
    {
        if (z < 33000d || z > 285000d) return false;
        bool coastal = cls == LandscapeClass.CoastalBelt;
        if (!(coastal ? w.ClassRing(a, z, cls, 500d, 6) : w.ClassRing(a, z, cls, 1500d))) return false;
        // Both ends and the middle of each of the four transects.
        foreach (double d in new[] { -65536d, -2048d, 0d, 2047d, 65535d })
            if (!SiteFreePoint(w, a + d, z)) return false;
        foreach (double d in new[] { -32768d, -2048d, 0d, 2047d, 32736d })
            if (!SiteFreePoint(w, a, z + d)) return false;
        return true;
    }

    // ---- measurement -------------------------------------------------------------------------------------------------------------------------------

    static Anchor Measure(LandscapeWorld.Worker w, int cls, double a, double z)
    {
        bool clampZero = (LandscapeClass)cls == LandscapeClass.CoastalBelt;
        double H(double around, double across)
        {
            double h = w.Ground(Wrap(around), across);
            return clampZero && h < 0d ? 0d : h;
        }
        var an = new Anchor { Cls = cls, Around = a, Across = z };
        var fine = new double[2][];
        for (int dir = 0; dir < 2; dir++)
        {
            double[] h = new double[FineN];
            for (int n = 0; n < FineN; n++) h[n] = dir == 0 ? H(a - FineN / 2 + n, z) : H(a, z - FineN / 2 + n);
            fine[dir] = h;
            var spec = Spectrum(h);
            an.FineBand[dir] = BandRms(spec, FineN * 1.0, 0, FineBandsTo);
            an.Relief[dir] = h.Max() - h.Min();
            int cn = dir == 0 ? CoarseAroundN : CoarseAcrossN;
            double[] c = new double[cn];
            for (int n = 0; n < cn; n++) c[n] = dir == 0 ? H(a + (n - cn / 2) * CoarseStep, z) : H(a, z + (n - cn / 2) * CoarseStep);
            var cspec = Spectrum(c);
            an.CoarseBand[dir] = BandRms(cspec, cn * CoarseStep, CoarseBandsFrom, Bands - 1);
            double[] m = new double[FineN / 32];
            for (int n = 0; n < m.Length; n++) m[n] = fine[dir][n * 32];
            an.MatchedBand[dir] = BandRms(Spectrum(m), FineN, 5, 8);
            an.DecadeRms[dir] = new double[Decades.Length];
            for (int d = 0; d < Decades.Length; d++)
                an.DecadeRms[dir][d] = Math.Sqrt(RangePower(Decades[d].Fine ? spec : cspec, Decades[d].Fine ? FineN * 1.0 : cn * CoarseStep, Decades[d].Lo, Decades[d].Hi));
        }
        for (int b = 0; b < SlopeBaselines.Length; b++)
        {
            int B = SlopeBaselines[b];
            var list = new List<double>(2 * FineN);
            for (int dir = 0; dir < 2; dir++)
                for (int n = 0; n + B < FineN; n++) list.Add(Math.Atan(Math.Abs(fine[dir][n + B] - fine[dir][n]) / B) * (180d / Math.PI));
            an.Slopes[b] = list.ToArray();
        }
        var s2 = new List<double>(400);
        for (int u = 0; u < 20; u++)
            for (int v = 0; v < 20; v++)
            {
                double pa = a + (u - 9.5) * 100d, pz = z + (v - 9.5) * 100d;
                double gx = (H(pa + 2, pz) - H(pa - 2, pz)) / 4d, gz = (H(pa, pz + 2) - H(pa, pz - 2)) / 4d;
                s2.Add(Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * (180d / Math.PI));
            }
        an.Slope2d = s2.ToArray();
        return an;
    }

    // ---- spectrum ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>One-sided power per bin k = 0..N/2 of the linearly detrended, Hann-windowed (periodic) series, divided by mean(w^2): the sum over k >= 1 is the series variance.</summary>
    internal static double[] Spectrum(double[] x)
    {
        int n = x.Length;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { sx += i; sy += x[i]; sxx += (double)i * i; sxy += i * x[i]; }
        double denom = n * sxx - sx * sx;
        double slope = (n * sxy - sx * sy) / denom, icpt = (sy - slope * sx) / n;
        var re = new double[n];
        var im = new double[n];
        double w2 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 * (1d - Math.Cos(2d * Math.PI * i / n));
            w2 += w * w;
            re[i] = (x[i] - (icpt + slope * i)) * w;
        }
        w2 /= n;
        Fft(re, im);
        var p = new double[n / 2 + 1];
        for (int k = 1; k <= n / 2; k++)
        {
            double pw = (re[k] * re[k] + im[k] * im[k]) / ((double)n * n * w2);
            p[k] = k == n / 2 ? pw : 2d * pw;
        }
        return p;
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2d * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    /// <summary>Band RMS for octave bands [2^(b+1), 2^(b+2)) m; bin k has wavelength span/k. Bands outside [from, to] stay NaN.</summary>
    static double[] BandRms(double[] power, double span, int from, int to)
    {
        var rms = new double[Bands];
        for (int b = 0; b < Bands; b++) rms[b] = double.NaN;
        for (int b = from; b <= to; b++)
        {
            double lo = Math.Pow(2, b + 1), hi = Math.Pow(2, b + 2);
            rms[b] = Math.Sqrt(RangePower(power, span, lo, hi));
        }
        return rms;
    }

    /// <summary>Summed power of the bins with lo &lt;= span / k &lt; hi.</summary>
    static double RangePower(double[] power, double span, double lo, double hi)
    {
        double sum = 0;
        for (int k = 1; k < power.Length; k++)
        {
            double lambda = span / k;
            if (lambda >= lo && lambda < hi) sum += power[k];
        }
        return sum;
    }

    static void SineCheck(List<string> failures)
    {
        // Spec asks for 256 m, which sits exactly on the 128-256 / 256-512 band edge: Hann leakage then puts about a sixth of the power in the neighbouring band
        // whatever the edge rule, so the 2 % test uses 362 m (geometric centre of the band). The edge case is printed for information.
        foreach (double lambda in new[] { 362d, 256d })
        {
            var x = new double[FineN];
            for (int i = 0; i < FineN; i++) x[i] = 3d * Math.Sin(2d * Math.PI * i / lambda);
            var rms = BandRms(Spectrum(x), FineN, 0, FineBandsTo);
            double total = Math.Sqrt(rms.Where(v => !double.IsNaN(v)).Sum(v => v * v));
            GD.Print($"LANDSCAPE_RELIEF sine 3 m @ {lambda} m: band 256-512 rms={rms[7]:F4} band 128-256 rms={rms[6]:F4} total={total:F4} (expect 2.1213)");
            if (lambda == 362d && Math.Abs(rms[7] - 3d / Math.Sqrt(2d)) > 0.02 * 3d / Math.Sqrt(2d)) failures.Add($"sine check: band rms {rms[7]:F4}, expected 2.1213 +- 2 %");
        }
    }

    // ---- statistics and report ---------------------------------------------------------------------------------------------------------------------

    static double Percentile(IList<double> values, double q)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        double pos = q * (v.Length - 1);
        int lo = (int)Math.Floor(pos), hi = Math.Min(v.Length - 1, lo + 1);
        return v[lo] + (v[hi] - v[lo]) * (pos - lo);
    }

    sealed class ClassStats
    {
        public LandscapeClass Cls; public int AnchorCount; public double Coverage;
        public double[] BandMed = new double[Bands], BandP90 = new double[Bands];
        public double[] FineMed = new double[Bands], CoarseMed = new double[Bands], MatchedMed = new double[Bands];
        public double[] DecMed = new double[Decades.Length], DecP90 = new double[Decades.Length];
        public double[][] Slope = new double[3][];  // [baseline] -> p50, p90, p99
        public double[] Slope2d = new double[3];    // p50, p95, max
        public double[] Relief = new double[2];     // p50, p95
        public List<(double, double)> Anchors = new();
    }

    sealed class Stats
    {
        public List<ClassStats> Classes = new();
        public string Json(string stamp, double seconds)
        {
            using var ms = new MemoryStream();
            using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                j.WriteStartObject();
                j.WriteNumber("schema", 1);
                j.WriteString("world_hash", LandscapeWorld.DefinitionHashHex);
                j.WriteNumber("anchors_per_class", AnchorsPerClass);
                j.WriteString("coastal_note", "CoastalBelt heights are clamped at 0 (water surface)");
                j.WriteStartArray("bands");
                for (int b = 0; b < Bands; b++) { j.WriteStartObject(); j.WriteNumber("lo_m", Math.Pow(2, b + 1)); j.WriteNumber("hi_m", Math.Pow(2, b + 2)); j.WriteString("source", b < FineUntilBand + 1 ? "fine" : "coarse"); j.WriteEndObject(); }
                j.WriteEndArray();
                j.WriteStartArray("classes");
                foreach (var c in Classes)
                {
                    j.WriteStartObject();
                    j.WriteString("class", c.Cls.ToString());
                    j.WriteNumber("anchors", c.AnchorCount);
                    j.WriteNumber("lattice_share", c.Coverage);
                    Arr(j, "band_rms_median", c.BandMed); Arr(j, "band_rms_p90", c.BandP90);
                    Arr(j, "band_rms_median_fine", c.FineMed); Arr(j, "band_rms_median_coarse", c.CoarseMed); Arr(j, "band_rms_median_matched_window_32m", c.MatchedMed);
                    j.WriteStartObject("decade_rms");
                    for (int d = 0; d < Decades.Length; d++) { j.WriteStartObject(Decades[d].Name); j.WriteNumber("median", c.DecMed[d]); j.WriteNumber("p90", c.DecP90[d]); j.WriteEndObject(); }
                    j.WriteEndObject();
                    j.WriteStartObject("slope_deg");
                    for (int b = 0; b < 3; b++) { j.WriteStartObject(SlopeBaselines[b] + "m"); j.WriteNumber("p50", c.Slope[b][0]); j.WriteNumber("p90", c.Slope[b][1]); j.WriteNumber("p99", c.Slope[b][2]); j.WriteEndObject(); }
                    j.WriteEndObject();
                    j.WriteStartObject("slope2d_4m_deg"); j.WriteNumber("p50", c.Slope2d[0]); j.WriteNumber("p95", c.Slope2d[1]); j.WriteNumber("max", c.Slope2d[2]); j.WriteEndObject();
                    j.WriteStartObject("relief_4km_m"); j.WriteNumber("p50", c.Relief[0]); j.WriteNumber("p95", c.Relief[1]); j.WriteEndObject();
                    j.WriteStartArray("anchor_positions");
                    foreach (var (a, z) in c.Anchors) { j.WriteStartArray(); j.WriteNumberValue(a); j.WriteNumberValue(z); j.WriteEndArray(); }
                    j.WriteEndArray();
                    j.WriteEndObject();
                }
                j.WriteEndArray();
                j.WriteStartObject("run");
                j.WriteString("stamp", stamp);
                j.WriteNumber("seconds", seconds);
                j.WriteEndObject();
                j.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
        }

        static void Arr(Utf8JsonWriter j, string name, double[] v)
        {
            j.WriteStartArray(name);
            foreach (double x in v) { if (double.IsNaN(x)) j.WriteNullValue(); else j.WriteNumberValue(x); }
            j.WriteEndArray();
        }

        static string F(double v, string fmt) => double.IsNaN(v) ? "-" : v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        static string BandName(int b) => FormatLen(Math.Pow(2, b + 1)) + "-" + FormatLen(Math.Pow(2, b + 2));
        static string FormatLen(double m) => m >= 1000 ? (m / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " km" : m.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";

        public string Markdown(string stamp, double seconds)
        {
            var sb = new StringBuilder();
            string head = "| " + string.Join(" | ", Classes.Select(c => c.Cls.ToString())) + " |";
            string rule = "|---|" + string.Concat(Classes.Select(_ => "---|"));
            sb.AppendLine($"Relief spectrum, world {LandscapeWorld.DefinitionHashHex}, stamp {stamp}, run time {seconds:F1} s. CoastalBelt heights are clamped at 0 (water surface).");
            sb.AppendLine();
            sb.AppendLine("Anchors per class (lattice share): " + string.Join(", ", Classes.Select(c => $"{c.Cls} {c.AnchorCount} ({c.Coverage * 100:F1} %)")));
            sb.AppendLine();
            sb.AppendLine("Band RMS height in metres, median (p90) over anchors and both directions. Wavelengths below 512 m come from the 4,096 m transects sampled at 1 m (local to the anchor), 512 m and above from the 32 m transects (65-131 km long, so they average over neighbouring landscape).");
            sb.AppendLine();
            sb.AppendLine("| Band | " + head.TrimStart('|').TrimStart()); sb.AppendLine(rule);
            for (int b = 0; b < Bands - 1; b++)
                sb.AppendLine($"| {BandName(b)} | " + string.Join(" | ", Classes.Select(c => $"{F(c.BandMed[b], "0.000")} ({F(c.BandP90[b], "0.000")})")) + " |");
            sb.AppendLine();
            sb.AppendLine("Decade RMS height in metres, median (p90). The 30-300 m decade is the one plan section 2.1 calls the rolling band.");
            sb.AppendLine();
            sb.AppendLine("| Decade | " + head.TrimStart('|').TrimStart()); sb.AppendLine(rule);
            for (int d = 0; d < Decades.Length; d++)
                sb.AppendLine($"| {Decades[d].Name} | " + string.Join(" | ", Classes.Select(c => $"{F(c.DecMed[d], "0.000")} ({F(c.DecP90[d], "0.000")})")) + " |");
            sb.AppendLine();
            sb.AppendLine("Slope in degrees (atan of the height difference over the baseline). Directional rows pool the 1 m transects of both directions; the 2D row uses central differences at 4 m on a 20 x 20 grid per anchor (100 m spacing).");
            sb.AppendLine();
            sb.AppendLine("| Slope | " + head.TrimStart('|').TrimStart()); sb.AppendLine(rule);
            for (int b = 0; b < 3; b++)
                foreach (var (name, idx) in new[] { ("p50", 0), ("p90", 1), ("p99", 2) })
                    sb.AppendLine($"| {SlopeBaselines[b]} m baseline {name} | " + string.Join(" | ", Classes.Select(c => F(c.Slope[b][idx], "0.00"))) + " |");
            foreach (var (name, idx) in new[] { ("p50", 0), ("p95", 1), ("max", 2) })
                sb.AppendLine($"| 2D 4 m {name} | " + string.Join(" | ", Classes.Select(c => F(c.Slope2d[idx], "0.00"))) + " |");
            sb.AppendLine();
            sb.AppendLine("4 km relief (max minus min over each 4,096 m transect), metres.");
            sb.AppendLine();
            sb.AppendLine("| Relief | " + head.TrimStart('|').TrimStart()); sb.AppendLine(rule);
            sb.AppendLine("| p50 | " + string.Join(" | ", Classes.Select(c => F(c.Relief[0], "0.0"))) + " |");
            sb.AppendLine("| p95 | " + string.Join(" | ", Classes.Select(c => F(c.Relief[1], "0.0"))) + " |");
            sb.AppendLine();
            sb.AppendLine("Method consistency, median band RMS in metres: 1 m sampling / 32 m sampling over the same 4,096 m window (must agree within 25 %) / wide 32 m transects (65-131 km, informational: they leave the anchor's class).");
            sb.AppendLine();
            sb.AppendLine("| Band | " + head.TrimStart('|').TrimStart()); sb.AppendLine(rule);
            foreach (int b in new[] { 5, 6, 7, 8 })
                sb.AppendLine($"| {BandName(b)} | " + string.Join(" | ", Classes.Select(c => $"{F(c.FineMed[b], "0.000")} / {F(c.MatchedMed[b], "0.000")} / {F(c.CoarseMed[b], "0.000")}")) + " |");
            return sb.ToString();
        }
    }

    static Stats BuildStats(Anchor[] anchors, int[] coverage, int latticeCount, List<string> failures, List<string> warnings)
    {
        var stats = new Stats();
        foreach (LandscapeClass cls in Classes)
        {
            var set = anchors.Where(a => a.Cls == (int)cls).ToArray();
            var cs = new ClassStats { Cls = cls, AnchorCount = set.Length, Coverage = coverage[(int)cls] / (double)latticeCount };
            foreach (var a in set) cs.Anchors.Add((a.Around, a.Across));
            if (set.Length == 0)
            {
                for (int b = 0; b < Bands; b++) cs.BandMed[b] = cs.BandP90[b] = cs.FineMed[b] = cs.CoarseMed[b] = cs.MatchedMed[b] = double.NaN;
                for (int d = 0; d < Decades.Length; d++) cs.DecMed[d] = cs.DecP90[d] = double.NaN;
                for (int b = 0; b < 3; b++) cs.Slope[b] = new[] { double.NaN, double.NaN, double.NaN };
                cs.Slope2d = new[] { double.NaN, double.NaN, double.NaN };
                cs.Relief = new[] { double.NaN, double.NaN };
                stats.Classes.Add(cs);
                continue;
            }
            for (int b = 0; b < Bands; b++)
            {
                var fine = set.SelectMany(a => new[] { a.FineBand[0][b], a.FineBand[1][b] }).ToList();
                var coarse = set.SelectMany(a => new[] { a.CoarseBand[0][b], a.CoarseBand[1][b] }).ToList();
                cs.FineMed[b] = Percentile(fine, 0.5);
                cs.CoarseMed[b] = Percentile(coarse, 0.5);
                cs.MatchedMed[b] = Percentile(set.SelectMany(a => new[] { a.MatchedBand[0][b], a.MatchedBand[1][b] }).ToList(), 0.5);
                var used = b <= FineUntilBand ? fine : coarse;
                cs.BandMed[b] = Percentile(used, 0.5);
                cs.BandP90[b] = Percentile(used, 0.9);
            }
            for (int d = 0; d < Decades.Length; d++)
            {
                var v = set.SelectMany(a => new[] { a.DecadeRms[0][d], a.DecadeRms[1][d] }).ToList();
                cs.DecMed[d] = Percentile(v, 0.5);
                cs.DecP90[d] = Percentile(v, 0.9);
            }
            for (int b = 0; b < 3; b++)
            {
                var v = set.SelectMany(a => a.Slopes[b]).ToList();
                cs.Slope[b] = new[] { Percentile(v, 0.5), Percentile(v, 0.9), Percentile(v, 0.99) };
            }
            var s2 = set.SelectMany(a => a.Slope2d).ToList();
            cs.Slope2d = new[] { Percentile(s2, 0.5), Percentile(s2, 0.95), s2.Max() };
            var rel = set.SelectMany(a => a.Relief).ToList();
            cs.Relief = new[] { Percentile(rel, 0.5), Percentile(rel, 0.95) };
            // Consistency: 128-256 m and 256-512 m, 1 m sampling against 32 m sampling over the same 4,096 m window, within 25 %. The 65-131 km coarse transects are not
            // comparable: they leave the anchor's class (see the report) and are compared only for information.
            foreach (int b in new[] { 6, 7 })
            {
                double f = cs.FineMed[b], c = cs.MatchedMed[b];
                double err = Math.Abs(f - c) / Math.Max(1e-9, c);
                GD.Print($"LANDSCAPE_RELIEF consistency {cls} band {b} fine={f:F4} matched32m={c:F4} diff={err * 100:F1} % (wide coarse {cs.CoarseMed[b]:F4})");
                if (!(err <= 0.25)) failures.Add($"consistency {cls} band {BandNameStatic(b)}: fine {f:F4} vs 32 m matched window {c:F4} ({err * 100:F1} %)");
            }
            stats.Classes.Add(cs);
        }
        var plains = stats.Classes.First(c => c.Cls == LandscapeClass.Plains);
        var hills = stats.Classes.First(c => c.Cls == LandscapeClass.HillCountry);
        if (!(plains.DecMed[1] < hills.DecMed[1])) warnings.Add($"sanity: Plains 30-300 m decade RMS {plains.DecMed[1]:F3} is not below HillCountry's {hills.DecMed[1]:F3}");
        return stats;
    }

    static string BandNameStatic(int b) => $"{Math.Pow(2, b + 1)}-{Math.Pow(2, b + 2)} m";
}
