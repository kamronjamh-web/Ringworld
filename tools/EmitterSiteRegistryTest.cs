using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Spec section 8 tests 1 and 2 for P1: registry determinism (golden, threads, seeds), invariants, and the
/// O(1) lookup against a brute-force scan. Also prints site statistics and timing.
/// </summary>
public partial class EmitterSiteRegistryTest : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const string Format = "emitter_site_registry_v2";

    public int Run(bool capture)
        => RunGolden(ProjectSettings.GlobalizePath("res://tools/fixtures/emitter_site_registry_golden.json"), capture);

    public static int RunGolden(string fixture, bool capture)
    {
        if (capture && File.Exists(fixture)) return Fail("Refusing to overwrite existing golden fixture: " + fixture);
        var failures = new List<string>();
        try
        {
            var golden = new Golden { Format = Format };
            foreach (bool gen17 in new[] { true, false })
            {
                string name = gen17 ? "gen17_v4_seed_1337" : "gen16_certified_seed_1337";
                using RingTerrainSampler sampler = gen17 ? EmitterSiteTestWorlds.CreateGen17(1337) : EmitterSiteTestWorlds.CreateGen16(1337);
                using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
                RunBackend(name, gen17, source, golden, failures);
            }
            RunSeedChecks(failures);
            if (capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(golden, JsonOptions) + System.Environment.NewLine);
                Console.WriteLine("EMITTER_SITE_REGISTRY_CAPTURE=DONE");
            }
            else
            {
                if (!File.Exists(fixture)) failures.Add("golden fixture missing");
                else CompareGolden(JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions), golden, failures);
            }
        }
        catch (Exception exception) { return Fail(exception.ToString()); }
        if (failures.Count != 0) return Fail("EMITTER_SITE_REGISTRY=FAIL " + string.Join(" | ", failures));
        Console.WriteLine("EMITTER_SITE_REGISTRY=PASS");
        return 0;
    }

    static void RunBackend(string name, bool gen17, INaturalHeightSource source, Golden golden, List<string> failures)
    {
        EmitterSiteRecipe recipe = EmitterSiteRecipe.Default(1337);
        EmitterSiteRegistry.ClearCache();

        // Cold (single cached call) vs warm cache.
        EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(recipe, source);
        double cold = registry.BuildMilliseconds;
        var warm = Stopwatch.StartNew();
        EmitterSiteRegistry again = EmitterSiteRegistry.GetOrBuild(recipe, source);
        warm.Stop();
        if (!ReferenceEquals(registry, again)) failures.Add(name + ": cache returned a different instance");
        Console.WriteLine($"EMITTER_SITE_TIMING {name} cold_build_ms={cold:F0} threads={registry.BuildThreads} cached_lookup_ms={warm.Elapsed.TotalMilliseconds:F4}");

        // Determinism: threads 1, 3, 8, and a repeat run.
        byte[] reference = registry.ToCanonicalBytes();
        foreach (int threads in new[] { 1, 3, 8 })
        {
            EmitterSiteRegistry other = EmitterSiteRegistry.Build(recipe, source, threads);
            if (!reference.AsSpan().SequenceEqual(other.ToCanonicalBytes())) failures.Add($"{name}: threads={threads} differs");
            if (other.Identity != registry.Identity) failures.Add($"{name}: threads={threads} identity differs");
            Console.WriteLine($"EMITTER_SITE_TIMING {name} uncached_build_ms={other.BuildMilliseconds:F0} threads={threads}");
        }

        CheckInvariants(name, registry, failures);
        CheckLookup(name, registry, failures);
        CheckNearestAndRadius(name, registry, failures);
        CheckYaw(name, registry, failures);
        PrintStats(name, registry);
        PrintMain(name + " seed=1337", registry);
        BenchmarkLookup(name, registry);

        var entry = new BackendGolden
        {
            Name = name, RecipeHash = recipe.Hash.ToString("X16"), RowPhaseM = recipe.RowPhaseM,
            NaturalIdentity = registry.NaturalIdentity.ToString("X16"), RegistryIdentity = registry.Identity.ToString("X16"),
            Land = registry.Sites.ToArray().Count(s => !s.IsIsland), Island = registry.Sites.ToArray().Count(s => s.IsIsland),
            Mountain = registry.Sites.ToArray().Count(s => s.IsMountain),
            MainRow = registry.MainRow, MainIndex = registry.MainIndex, MainFallback = registry.MainIsIslandFallback,
            RegularCount = registry.Sites.ToArray().Count(s => !s.IsMain), MainCount = registry.Sites.ToArray().Count(s => s.IsMain)
        };
        for (int i = 0; i < 12; i++)
        {
            EmitterSite s = registry[i * 50 % registry.Count];
            entry.Samples.Add(new SiteGolden { Index = s.Index, Around = s.Around, Across = s.Across, Pad = s.PadHeight, Beta = s.BetaDegrees, Flags = (int)s.Flags, SiteHash = s.SiteHash.ToString("X16") });
        }
        entry.Samples.Add(new SiteGolden { Index = registry.MainIndex, Around = registry.MainSite.Around, Across = registry.MainSite.Across, Pad = registry.MainSite.PadHeight, Beta = registry.MainSite.BetaDegrees, Flags = (int)registry.MainSite.Flags, SiteHash = registry.MainSite.SiteHash.ToString("X16") });
        golden.Backends.Add(entry);
    }

    static void CheckInvariants(string name, EmitterSiteRegistry r, List<string> failures)
    {
        EmitterSiteRecipe recipe = r.Recipe;
        if (r.Count != 595) failures.Add($"{name}: site count {r.Count}");
        var unique = new HashSet<(long, int)>();
        double halfAcross = (recipe.ColumnOffsetsM[1] - recipe.ColumnOffsetsM[0]) / 2d, halfAround = recipe.RowPitchM / 2d;
        double maxReach = 0d;
        for (int i = 0; i < r.Count; i++)
        {
            EmitterSite s = r[i];
            unique.Add((s.Around, s.Across));
            if (s.Index != i) failures.Add($"{name}: site {i} index mismatch");
            if ((s.Around & 1L) != 0 || (s.Across & 1) != 0) failures.Add($"{name}: site {i} off the 2 m lattice");
            if (s.Around < 0 || s.Around >= Circumference) failures.Add($"{name}: site {i} around out of range");
            if (s.NudgeMeters > recipe.NudgeRadiusM + 3d) failures.Add($"{name}: site {i} nudge {s.NudgeMeters}");
            if ((s.Flags & EmitterSiteFlags.Ocean) != 0 && (s.NudgeMeters != 0f || !s.IsIsland)) failures.Add($"{name}: ocean site {i} moved or not an island");
            if (s.IsIsland == ((s.Flags & EmitterSiteFlags.Land) != 0)) failures.Add($"{name}: site {i} land/island flags");
            double reach = s.InfluenceRadius + recipe.NudgeRadiusM;
            maxReach = Math.Max(maxReach, reach);
            if (reach >= Math.Min(halfAcross, halfAround)) failures.Add($"{name}: site {i} influence+nudge {reach} >= half pitch");
            if (s.Across - s.InfluenceRadius - 1000d < 0 || s.Across + s.InfluenceRadius + 1000d > Width) failures.Add($"{name}: site {i} too close to a wall");
            if (s.PadHeight < (s.IsIsland ? 24 : 8) - 0) failures.Add($"{name}: site {i} pad {s.PadHeight} below minimum");
        }
        if (unique.Count != 595) failures.Add($"{name}: {unique.Count} unique positions");
        CheckMain(name, r, failures);
        double minDistance = double.MaxValue;
        for (int i = 0; i < r.Count; i++)
        for (int j = i + 1; j < r.Count; j++)
        {
            long d = Math.Abs(r[i].Around - r[j].Around);
            d = Math.Min(d, Circumference - d);
            double dx = r[i].Across - r[j].Across;
            minDistance = Math.Min(minDistance, Math.Sqrt((double)d * d + dx * dx));
        }
        if (minDistance < 45_000d) failures.Add($"{name}: pairwise distance {minDistance:F0} m < 45 km");
        Console.WriteLine($"EMITTER_SITE_INVARIANTS {name} min_pairwise_km={minDistance / 1000d:F2} max_influence_plus_nudge_km={maxReach / 1000d:F2} half_across_pitch_km={halfAcross / 1000d:F1}");
    }

    static void CheckLookup(string name, EmitterSiteRegistry r, List<string> failures)
    {
        var rng = new Random(12345);
        long checkedPoints = 0, hits = 0;
        void Check(long around, double across)
        {
            checkedPoints++;
            bool fast = r.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
            int brute = r.FindInfluencingSiteBruteForce(around, across, out int matches);
            if (matches > 1) failures.Add($"{name}: {matches} sites influence ({around},{across})");
            if (fast != (brute >= 0) || (fast && index != brute))
            {
                failures.Add($"{name}: lookup mismatch at ({around},{across}) fast={fast}/{index} brute={brute}");
                return;
            }
            if (fast)
            {
                hits++;
                if (Math.Abs(du) > Circumference / 2 || du * du + dw * dw >= (double)r[index].InfluenceRadius * r[index].InfluenceRadius)
                    failures.Add($"{name}: bad du/dw at ({around},{across})");
            }
        }

        // Uniform random points over the whole ring, including negative and multi-turn around values.
        for (int i = 0; i < 300_000; i++)
            Check((long)(rng.NextDouble() * Circumference * 3d) - Circumference, rng.NextDouble() * (Width + 2000) - 1000d);
        // Targeted: inside, on and outside every influence boundary, random angles.
        for (int i = 0; i < r.Count; i++)
        {
            EmitterSite s = r[i];
            for (int k = 0; k < 96; k++)
            {
                double angle = rng.NextDouble() * Math.Tau;
                double[] scales = { 0.0, 0.5, 0.999, 0.9999, 1.0, 1.0001, 1.001, 1.5 };
                double radius = s.InfluenceRadius * scales[k % scales.Length];
                Check(s.Around + (long)Math.Round(radius * Math.Cos(angle)), s.Across + radius * Math.Sin(angle));
            }
        }
        // Ring seam, walls, and corners.
        long[] seam = { 0, 1, 2, Circumference - 1, Circumference, Circumference + 1, -1, -Circumference, Circumference * 2 };
        double[] edges = { 0, 1, 1000, 26_499.5, 26_500, 52_999, 53_000, 79_500, 291_500, Width - 1000, Width - 1, Width, -10, Width + 10 };
        foreach (long a in seam) foreach (double x in edges) Check(a, x);
        for (int i = 0; i < r.Count; i++)
        {
            long seamA = r[i].Around < Circumference / 2 ? 0 : Circumference - 1;
            if (Math.Min(r[i].Around, Circumference - r[i].Around) < r[i].InfluenceRadius + 1000d)
                for (int k = 0; k < 64; k++) Check(seamA + k * 500 - 16_000, r[i].Across + (k % 8 - 4) * 1500d);
        }
        // Near the walls at every row.
        for (int row = 0; row < 100; row++)
            for (int k = 0; k < 20; k++)
            {
                long a = (long)(row * r.Recipe.RowPitchM + r.Recipe.RowPhaseM) + (k - 10) * 400L;
                Check(a, 0.0 + k * 40d);
                Check(a, Width - k * 40d);
            }
        // The empty main row and the main site: sweeps across the whole width at many offsets along the row, and around the main disc.
        {
            EmitterSite m = r.MainSite;
            long[] alongOffsets = { -157_000, -150_000, -100_000, -30_000, -23_000, -21_000, -6_001, -1_000, 0, 1, 1_000, 5_999, 6_001, 20_000, 22_000, 23_000, 30_000, 100_000, 156_000 };
            for (double x = -500d; x <= Width + 500d; x += 3_500d)
                foreach (long off in alongOffsets) Check(m.Around + off, x);
            double[] radii = { 0d, 500d, 1_149d, 1_151d, 3_999d, 4_001d, 5_000d, 5_999d, 6_001d, 12_000d, m.InfluenceRadius - 1d, m.InfluenceRadius, m.InfluenceRadius + 1d, m.InfluenceRadius * 1.2d };
            foreach (double radius in radii)
                for (int k = 0; k < 90; k++)
                {
                    double angle = k * Math.Tau / 90d + 0.01d;
                    Check(m.Around + (long)Math.Round(radius * Math.Cos(angle)), m.Across + radius * Math.Sin(angle));
                }
        }
        Console.WriteLine($"EMITTER_SITE_LOOKUP {name} points={checkedPoints} influenced={hits} (all equal to brute force: {(failures.Count == 0)})");
    }

    static double WrappedDelta(long a, long b)
    {
        long d = Math.Abs(a - b) % Circumference;
        return Math.Min(d, Circumference - d);
    }

    static void CheckNearestAndRadius(string name, EmitterSiteRegistry r, List<string> failures)
    {
        var rng = new Random(777);
        var points = new List<(long, double)>();
        for (int i = 0; i < 100_000; i++)
            points.Add(((long)(rng.NextDouble() * Circumference * 2d) - Circumference / 2, rng.NextDouble() * (Width + 20_000) - 10_000d));
        for (int i = 0; i < r.Count; i++)
            for (int k = 0; k < 40; k++)
            {
                double angle = rng.NextDouble() * Math.Tau, radius = rng.NextDouble() * 80_000d;
                points.Add((r[i].Around + (long)(radius * Math.Cos(angle)), r[i].Across + radius * Math.Sin(angle)));
            }
        foreach (long a in new long[] { 0, 1, Circumference - 1, Circumference, -1 })
            foreach (double x in new double[] { -5000, 0, 1000, 26_500, 159_000, Width - 1, Width, Width + 5000 })
                points.Add((a, x));
        for (int row = 0; row < 100; row++)
            points.Add(((long)(row * r.Recipe.RowPitchM + r.Recipe.RowPhaseM + r.Recipe.RowPitchM / 2d), 159_000d)); // between rows
        // The empty main row: random and structured points across it; nearest may be the main site or a neighbouring row's site.
        for (int k = 0; k < 400; k++)
            points.Add((r.MainSite.Around + (long)(rng.NextDouble() * 330_000d - 165_000d), rng.NextDouble() * Width));
        foreach (double x in new double[] { 0, 5_000, 26_500, 79_500, 132_500, 159_000, 185_500, 291_500, Width })
            foreach (long off in new long[] { -157_000, -78_000, -20_000, 0, 20_000, 78_000, 157_000 })
                points.Add((r.MainSite.Around + off, x));

        Span<int> found = stackalloc int[r.Count];
        int radiusChecks = 0;
        foreach ((long around, double across) in points)
        {
            bool ok = r.TryGetNearestSite(around, across, out int index, out double du, out double dw);
            long wrapped = ((around % Circumference) + Circumference) % Circumference;
            double best = double.MaxValue; int bestIndex = -1;
            for (int i = 0; i < r.Count; i++)
            {
                double dx = WrappedDelta(wrapped, r[i].Around), dy = across - r[i].Across;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; bestIndex = i; }
            }
            if (!ok || index != bestIndex || Math.Abs(du * du + dw * dw - best) > 1e-3)
            {
                failures.Add($"{name}: nearest mismatch at ({around},{across}) got {index} want {bestIndex}");
                break;
            }
            if (Math.Abs(du) > Circumference / 2) { failures.Add($"{name}: nearest du out of range"); break; }
        }
        // Radius enumeration, including 14-16 km streamer radii and radii wider than a grid cell.
        double[] radii = { 0d, 500d, 14_000d, 16_000d, 40_000d, 120_000d, 400_000d };
        for (int p = 0; p < points.Count; p += 7)
        {
            (long around, double across) = points[p];
            double radius = radii[(p / 7) % radii.Length];
            int count = r.GetSitesWithinRadius(around, across, radius, found);
            long wrapped = ((around % Circumference) + Circumference) % Circumference;
            int expected = 0; bool mismatch = false;
            for (int i = 0; i < r.Count; i++)
            {
                double dx = WrappedDelta(wrapped, r[i].Around), dy = across - r[i].Across;
                if (dx * dx + dy * dy > radius * radius) continue;
                if (expected >= count || found[expected] != i) mismatch = true;
                expected++;
            }
            radiusChecks++;
            if (expected != count || mismatch)
            {
                failures.Add($"{name}: radius enumeration mismatch at ({around},{across}) r={radius} got {count} want {expected}");
                break;
            }
        }
        // Truncated output reports the full count without overrunning.
        int truncated = r.GetSitesWithinRadius(0, 159_000d, 500_000d, stackalloc int[4]);
        if (truncated < 4) failures.Add($"{name}: truncated enumeration count {truncated}");
        Console.WriteLine($"EMITTER_SITE_NEAREST {name} nearest_points={points.Count} radius_checks={radiusChecks} (vs brute force)");
    }

    /// <summary>
    /// The asset-space EntryApproach marker (67.5 degrees in XZ) rotated by the site yaw about +Y must point along the
    /// site bearing in the Terrain3D frame, where local X = around and local Z = -across.
    /// </summary>
    static void CheckYaw(string name, EmitterSiteRegistry r, List<string> failures)
    {
        string scene = File.ReadAllText(ProjectSettings.GlobalizePath("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn"));
        int at = scene.IndexOf("[node name=\"EntryApproach\"", StringComparison.Ordinal);
        int t = at < 0 ? -1 : scene.IndexOf("Transform3D(", at, StringComparison.Ordinal);
        if (t < 0) { failures.Add(name + ": EntryApproach marker not found"); return; }
        string[] parts = scene.Substring(t + 12, scene.IndexOf(')', t) - t - 12).Split(',');
        var marker = new Vector3(float.Parse(parts[9], System.Globalization.CultureInfo.InvariantCulture), 0f,
            float.Parse(parts[11], System.Globalization.CultureInfo.InvariantCulture));
        double markerAngle = Math.Atan2(marker.Z, marker.X) * 180d / Math.PI;
        if (Math.Abs(markerAngle - 67.5) > 0.01) failures.Add($"{name}: marker angle {markerAngle}, spec assumes 67.5");
        double worst = 0d;
        for (int i = 0; i < r.Count; i++)
        {
            EmitterSite s = r[i];
            Vector3 world = new Basis(Vector3.Up, (float)(s.YawDegrees * Math.PI / 180d)) * marker;
            double around = world.X, across = -world.Z; // Terrain3D frame, AcrossDirection = -1
            double bearing = Math.Atan2(across, around) * 180d / Math.PI;
            double diff = Math.Abs(((bearing - s.BetaDegrees) % 360d + 540d) % 360d - 180d);
            worst = Math.Max(worst, diff);
            if (diff > 0.05) { failures.Add($"{name}: site {i} entry bearing {bearing:F3} != beta {s.BetaDegrees:F3}"); break; }
        }
        Console.WriteLine($"EMITTER_SITE_YAW {name} worst_error_deg={worst:F5} marker_deg={markerAngle:F3}");
    }

    static void PrintStats(string name, EmitterSiteRegistry r)
    {
        EmitterSite[] sites = r.Sites.ToArray();
        int land = sites.Count(s => !s.IsIsland), island = sites.Count(s => s.IsIsland), mountain = sites.Count(s => s.IsMountain);
        int ocean = sites.Count(s => (s.Flags & EmitterSiteFlags.Ocean) != 0);
        int enclosure = sites.Count(s => (s.Flags & EmitterSiteFlags.EnclosureRaise) != 0);
        int conflict = sites.Count(s => (s.Flags & EmitterSiteFlags.CutFillConflict) != 0);
        int nudged = sites.Count(s => (s.Flags & EmitterSiteFlags.Nudged) != 0);
        Console.WriteLine($"EMITTER_SITE_STATS {name} sites={sites.Length} land={land} island={island} (ocean-origin={ocean}, coastal={island - ocean}) mountain={mountain} enclosure_raise={enclosure} cut_fill_conflict={conflict} nudged={nudged}");
        int[] pads = sites.Select(s => s.PadHeight).OrderBy(v => v).ToArray();
        Console.WriteLine($"EMITTER_SITE_STATS {name} pad_m min={pads[0]} p10={pads[pads.Length / 10]} median={pads[pads.Length / 2]} p90={pads[pads.Length * 9 / 10]} max={pads[^1]}");
        int[] landPads = sites.Where(s => !s.IsIsland).Select(s => s.PadHeight).OrderBy(v => v).ToArray();
        if (landPads.Length > 0)
            Console.WriteLine($"EMITTER_SITE_STATS {name} land_pad_m min={landPads[0]} median={landPads[landPads.Length / 2]} max={landPads[^1]}");
        float[] nudges = sites.Where(s => !s.IsIsland || (s.Flags & EmitterSiteFlags.Ocean) == 0).Select(s => s.NudgeMeters).OrderBy(v => v).ToArray();
        Console.WriteLine($"EMITTER_SITE_STATS {name} land_nudge_m mean={nudges.Average():F0} median={nudges[nudges.Length / 2]:F0} max={nudges[^1]:F0} unmoved={nudges.Count(v => v == 0f)}/{nudges.Length}");
        float[] cuts = sites.Where(s => !s.IsIsland).Select(s => s.MaxCutMeters).OrderBy(v => v).ToArray();
        float[] fills = sites.Where(s => !s.IsIsland).Select(s => s.MaxFillMeters).OrderBy(v => v).ToArray();
        if (cuts.Length > 0)
            Console.WriteLine($"EMITTER_SITE_STATS {name} land_max_cut_m median={cuts[cuts.Length / 2]:F0} p90={cuts[cuts.Length * 9 / 10]:F0} max={cuts[^1]:F0}; max_fill_m median={fills[fills.Length / 2]:F0} p90={fills[fills.Length * 9 / 10]:F0} max={fills[^1]:F0}");
        int[] hist = new int[6];
        foreach (float n in nudges) hist[n <= 0f ? 0 : Math.Min(5, (int)Math.Ceiling((n - 3f) / 1000f))]++; // 3 m tolerance for lattice snapping
        Console.WriteLine($"EMITTER_SITE_STATS {name} nudge_hist 0m={hist[0]} <=1km={hist[1]} <=2km={hist[2]} <=3km={hist[3]} <=4km={hist[4]} >4km={hist[5]}");
        Console.WriteLine($"EMITTER_SITE_STATS {name} influence_km min={sites.Min(s => s.InfluenceRadius) / 1000f:F2} max={sites.Max(s => s.InfluenceRadius) / 1000f:F2}");
    }

    static void BenchmarkLookup(string name, EmitterSiteRegistry r)
    {
        const int Count = 8_000_000;
        var rng = new Random(99);
        long[] around = new long[4096]; double[] across = new double[4096];
        for (int i = 0; i < around.Length; i++) { around[i] = (long)(rng.NextDouble() * Circumference); across[i] = rng.NextDouble() * Width; }
        int sink = 0;
        for (int i = 0; i < 200_000; i++) sink += r.TryGetInfluencingSite(around[i & 4095], across[i & 4095], out int idx, out _, out _) ? idx : 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Count; i++) sink += r.TryGetInfluencingSite(around[i & 4095], across[i & 4095], out int idx, out _, out _) ? idx : 1;
        watch.Stop();
        Console.WriteLine($"EMITTER_SITE_PERF {name} lookup_ns_per_op={watch.Elapsed.TotalMilliseconds * 1e6 / Count:F1} ops={Count} (assembly configuration: {(IsOptimized() ? "optimized" : "debug")}) sink={sink}");
    }

    static bool IsOptimized()
    {
        foreach (object attr in typeof(EmitterSiteRegistryTest).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false))
            if (attr is DebuggableAttribute d) return !d.IsJITOptimizerDisabled;
        return true;
    }

    /// <summary>
    /// Exactly one main site, last index, on the centre line, its row empty, 99 other rows with exactly six regulars each,
    /// compact indices consistent with RegularIndex, kind-specific radii, and the island-or-land consistency of the main record.
    /// </summary>
    static void CheckMain(string name, EmitterSiteRegistry r, List<string> failures)
    {
        EmitterSiteRecipe recipe = r.Recipe;
        EmitterSite[] sites = r.Sites.ToArray();
        int mains = sites.Count(s => s.Kind == SiteKind.Main), regulars = sites.Count(s => s.Kind == SiteKind.Regular);
        if (mains != 1 || regulars != 594 || r.RegularCount != 594) failures.Add($"{name}: kinds main={mains} regular={regulars}");
        if (r.MainIndex != 594 || r.MainSite.Index != 594 || !r.MainSite.IsMain) failures.Add($"{name}: main index {r.MainIndex}");
        EmitterSite m = r.MainSite;
        if (m.Across != 159_000 || m.Across != (int)(recipe.WidthM / 2d)) failures.Add($"{name}: main across {m.Across} is not W/2");
        double pitch = recipe.RowPitchM;
        int RowOf(long around) { int row = (int)Math.Floor((around - recipe.RowPhaseM) / pitch + 0.5); return ((row % 100) + 100) % 100; }
        if (RowOf(m.Around) != r.MainRow) failures.Add($"{name}: main around row {RowOf(m.Around)} != MainRow {r.MainRow}");
        if (m.NudgeMeters > recipe.NudgeRadiusM + 3d) failures.Add($"{name}: main nudge {m.NudgeMeters}");
        // Along-ring only: the distance to the grid point equals the around offset because across is untouched.
        long gridAround = (long)EmitterSiteRecipe.Snap2(recipe.RowPhaseM + r.MainRow * pitch);
        long da = Math.Abs(m.Around - gridAround); da = Math.Min(da, Circumference - da);
        if (Math.Abs(da - m.NudgeMeters) > 3d) failures.Add($"{name}: main nudge {m.NudgeMeters} is not along-ring only (around offset {da})");
        // Row census.
        var perRow = new int[100];
        foreach (EmitterSite s in sites.Where(s => !s.IsMain)) perRow[RowOf(s.Around)]++;
        for (int row = 0; row < 100; row++)
        {
            int want = row == r.MainRow ? 0 : 6;
            if (perRow[row] != want) failures.Add($"{name}: row {row} has {perRow[row]} regular sites, want {want}");
        }
        // Compact index layout: RegularIndex(row, col) holds the regular site of that grid cell.
        for (int row = 0; row < 100; row++)
        {
            if (row == r.MainRow) continue;
            for (int col = 0; col < 6; col++)
            {
                EmitterSite s = r[r.RegularIndex(row, col)];
                long dr = Math.Abs(s.Around - (long)EmitterSiteRecipe.Snap2(recipe.RowPhaseM + row * pitch)); dr = Math.Min(dr, Circumference - dr);
                if (s.Kind != SiteKind.Regular || dr > recipe.NudgeRadiusM + 3d || Math.Abs(s.Across - recipe.ColumnOffsetsM[col]) > recipe.NudgeRadiusM + 3d)
                    failures.Add($"{name}: RegularIndex({row},{col}) -> site {s.Index} not in that cell");
            }
        }
        // Main-site kind constants: influence at least the skirt, island consistency, no disc overlap with any other site.
        EmitterSiteKindShape shape = recipe.Main;
        if (m.InfluenceRadius < shape.SkirtMaxRadiusM) failures.Add($"{name}: main influence {m.InfluenceRadius} < skirt");
        if (m.IsIsland && m.InfluenceRadius < shape.ShelfRadiusM) failures.Add($"{name}: main island influence {m.InfluenceRadius} < shelf radius");
        if (m.IsIsland == ((m.Flags & EmitterSiteFlags.Land) != 0)) failures.Add($"{name}: main land/island flags");
        if (r.MainIsIslandFallback && !m.IsIsland) failures.Add($"{name}: fallback main is not an island");
        if (m.IsIsland && m.PadHeight < 24) failures.Add($"{name}: main island pad {m.PadHeight}");
        double minGap = double.MaxValue;
        foreach (EmitterSite s in sites.Where(s => !s.IsMain))
        {
            long d = Math.Abs(m.Around - s.Around); d = Math.Min(d, Circumference - d);
            double dx = m.Across - s.Across;
            minGap = Math.Min(minGap, Math.Sqrt((double)d * d + dx * dx) - m.InfluenceRadius - s.InfluenceRadius);
        }
        if (minGap <= 0d) failures.Add($"{name}: main influence overlaps another site by {-minGap:F0} m");
        Console.WriteLine($"EMITTER_SITE_MAIN_INVARIANTS {name} main_index={r.MainIndex} regulars={r.RegularCount} main_row={r.MainRow} min_gap_to_other_discs_km={minGap / 1000d:F1} influence_plus_nudge_km={(m.InfluenceRadius + recipe.NudgeRadiusM) / 1000d:F2}");
    }

    static void PrintMain(string label, EmitterSiteRegistry r)
    {
        EmitterSite m = r.MainSite;
        string cls = m.IsIsland ? ((m.Flags & EmitterSiteFlags.Ocean) != 0 ? "island(ocean grid)" : "island(coastal/promoted)") : (m.IsMountain ? "land(mountain)" : "land");
        Console.WriteLine($"EMITTER_SITE_MAIN {label} row={r.MainRow} around={m.Around} across={m.Across} class={cls} pad_m={m.PadHeight} max_cut_m={m.MaxCutMeters:F0} max_fill_m={m.MaxFillMeters:F0} " +
            $"beta={m.BetaDegrees:F1} yaw={m.YawDegrees:F1} nudge_m={m.NudgeMeters:F0} influence_km={m.InfluenceRadius / 1000f:F2} flags={m.Flags} fallback={r.MainIsIslandFallback} acceptable_rows={r.AcceptableMainRowCount} site_hash={m.SiteHash:X16}");
    }

    sealed class SyntheticSource : INaturalHeightSource
    {
        readonly Func<long, int, double> _height; readonly ulong _identity;
        public SyntheticSource(Func<long, int, double> height, ulong identity) { _height = height; _identity = identity; }
        public double NaturalHeight(long around, int across) => _height(around, across);
        public double SeaLevel => 0d;
        public double MinPossibleHeight => -4000d;
        public ulong Identity => _identity;
        public INaturalHeightSource CreateWorkerCopy() => new SyntheticSource(_height, _identity);
        public bool TryGetExclusionZone(out long around, out int across, out double radiusMeters) { around = 0; across = 0; radiusMeters = 0d; return false; }
        public void Dispose() { }
    }

    /// <summary>Synthetic worlds: all land (main must be land on a seed-ranked row) and all ocean (island fallback on the seed-preferred row).</summary>
    static void RunSyntheticMain(List<string> failures)
    {
        foreach (long seed in new long[] { 1337, 7331, 424242, 0x1_0000_0539L })
        {
            EmitterSiteRecipe recipe = EmitterSiteRecipe.Default(seed);
            using var land = new SyntheticSource((a, x) => 120d + 30d * Math.Sin(a * 1e-4) * Math.Cos(x * 1e-4), 0xA11CE000UL + (ulong)seed);
            using var ocean = new SyntheticSource((a, x) => -900d, 0x0CEA0000UL + (ulong)seed);
            EmitterSiteRegistry rl = EmitterSiteRegistry.Build(recipe, land, 4), ro = EmitterSiteRegistry.Build(recipe, ocean, 4);
            CheckMain($"synthetic_land_seed_{seed}", rl, failures);
            CheckMain($"synthetic_ocean_seed_{seed}", ro, failures);
            if (rl.MainIsIslandFallback || rl.MainSite.IsIsland) failures.Add($"synthetic land seed {seed}: main is not land");
            if (!ro.MainIsIslandFallback || !ro.MainSite.IsIsland || ro.MainRow != recipe.SeedPreferredMainRow || ro.MainSite.NudgeMeters != 0f)
                failures.Add($"synthetic ocean seed {seed}: main is not the un-nudged island on the seed-preferred row {recipe.SeedPreferredMainRow} (got {ro.MainRow})");
            // Determinism across thread counts for the synthetic worlds as well.
            foreach (int threads in new[] { 1, 3, 8 })
            {
                if (!EmitterSiteRegistry.Build(recipe, land, threads).ToCanonicalBytes().AsSpan().SequenceEqual(rl.ToCanonicalBytes())) failures.Add($"synthetic land threads={threads} differs");
                if (!EmitterSiteRegistry.Build(recipe, ocean, threads).ToCanonicalBytes().AsSpan().SequenceEqual(ro.ToCanonicalBytes())) failures.Add($"synthetic ocean threads={threads} differs");
            }
            PrintMain($"synthetic_land seed={seed}", rl);
            PrintMain($"synthetic_ocean seed={seed}", ro);
        }
        var preferred = new HashSet<int>();
        for (long seed = 1; seed <= 40; seed++) preferred.Add(EmitterSiteRecipe.Default(seed).SeedPreferredMainRow);
        if (preferred.Count < 15) failures.Add($"seed-preferred main row takes only {preferred.Count} values over 40 seeds");
        Console.WriteLine($"EMITTER_SITE_MAIN_SEED_ROWS distinct_seed_preferred_rows_over_40_seeds={preferred.Count}");
    }

    /// <summary>Main row/class on more real worlds (both backends); the gen-17 main row must vary with the seed.</summary>
    static void RunMainSeedReport(List<string> failures, EmitterSiteRegistry r1337, EmitterSiteRegistry r7331)
    {
        var rows17 = new HashSet<int> { r1337.MainRow, r7331.MainRow };
        PrintMain("gen17_v4 seed=7331", r7331);
        foreach (int seed in new[] { 424242, 20260101 })
        {
            using RingTerrainSampler s17 = EmitterSiteTestWorlds.CreateGen17(seed);
            using INaturalHeightSource src17 = EmitterSiteHeightSources.Create(s17);
            EmitterSiteRegistry reg17 = EmitterSiteRegistry.Build(EmitterSiteRecipe.Default(seed), src17);
            CheckMain($"gen17_v4_seed_{seed}", reg17, failures);
            PrintMain($"gen17_v4 seed={seed}", reg17);
            rows17.Add(reg17.MainRow);
            using RingTerrainSampler s16 = EmitterSiteTestWorlds.CreateGen16(seed);
            using INaturalHeightSource src16 = EmitterSiteHeightSources.Create(s16);
            EmitterSiteRegistry reg16 = EmitterSiteRegistry.Build(EmitterSiteRecipe.Default(seed), src16);
            CheckMain($"gen16_certified_seed_{seed}", reg16, failures);
            PrintMain($"gen16_certified seed={seed}", reg16);
        }
        using (RingTerrainSampler s16b = EmitterSiteTestWorlds.CreateGen16(7331))
        using (INaturalHeightSource src16b = EmitterSiteHeightSources.Create(s16b))
            PrintMain("gen16_certified seed=7331", EmitterSiteRegistry.Build(EmitterSiteRecipe.Default(7331), src16b));
        if (rows17.Count < 3) failures.Add($"gen-17 main row did not vary with the seed ({rows17.Count} distinct rows over 4 seeds)");
        Console.WriteLine($"EMITTER_SITE_MAIN_SEEDS gen17_distinct_main_rows_over_4_seeds={rows17.Count}");
    }

    static void RunSeedChecks(List<string> failures)
    {
        EmitterSiteRecipe a = EmitterSiteRecipe.Default(1337), a2 = EmitterSiteRecipe.Default(1337);
        EmitterSiteRecipe b = EmitterSiteRecipe.Default(7331), big = EmitterSiteRecipe.Default(0x1_0000_0539L);
        if (a.Hash != a2.Hash || a.RowPhaseM != a2.RowPhaseM) failures.Add("recipe not deterministic for equal seeds");
        if (a.RowPhaseM == b.RowPhaseM || a.Hash == b.Hash) failures.Add("seed 7331 did not change phase/hash");
        if (a.RowPhaseM == big.RowPhaseM || big.RowPhaseM == b.RowPhaseM || a.Hash == big.Hash) failures.Add("seed above 2^32 did not change phase/hash");
        foreach (EmitterSiteRecipe recipe in new[] { a, b, big })
            if (recipe.RowPhaseM < 0 || recipe.RowPhaseM >= recipe.RowPitchM || (recipe.RowPhaseM % 2d) != 0d) failures.Add("phase out of range or not 2 m snapped");
        Console.WriteLine($"EMITTER_SITE_SEEDS phase_1337={a.RowPhaseM} phase_7331={b.RowPhaseM} phase_2p32+1337={big.RowPhaseM}");

        // A different seed must also give a different site list on a real world.
        using var s1337 = EmitterSiteTestWorlds.CreateGen17(1337);
        using var s7331 = EmitterSiteTestWorlds.CreateGen17(7331);
        using INaturalHeightSource src1 = EmitterSiteHeightSources.Create(s1337), src2 = EmitterSiteHeightSources.Create(s7331);
        EmitterSiteRegistry r1 = EmitterSiteRegistry.GetOrBuild(a, src1), r2 = EmitterSiteRegistry.GetOrBuild(b, src2);
        if (r1.Identity == r2.Identity || r1[0].Around == r2[0].Around) failures.Add("different seeds produced the same site list");

        RunSyntheticMain(failures);
        RunMainSeedReport(failures, r1, r2);
    }

    static void CompareGolden(Golden? expected, Golden actual, List<string> failures)
    {
        if (expected == null) { failures.Add("golden fixture unreadable"); return; }
        if (expected.Format != actual.Format) failures.Add("golden format");
        foreach (BackendGolden now in actual.Backends)
        {
            BackendGolden? want = expected.Backends.Find(b => b.Name == now.Name);
            if (want == null) { failures.Add(now.Name + ": missing golden"); continue; }
            if (want.RecipeHash != now.RecipeHash) failures.Add(now.Name + ": recipe hash " + want.RecipeHash + " -> " + now.RecipeHash);
            if (want.RowPhaseM != now.RowPhaseM) failures.Add(now.Name + ": row phase");
            if (want.NaturalIdentity != now.NaturalIdentity) failures.Add(now.Name + ": natural identity " + want.NaturalIdentity + " -> " + now.NaturalIdentity);
            if (want.RegistryIdentity != now.RegistryIdentity) failures.Add(now.Name + ": registry identity " + want.RegistryIdentity + " -> " + now.RegistryIdentity);
            if (want.Land != now.Land || want.Island != now.Island || want.Mountain != now.Mountain) failures.Add(now.Name + ": kind counts");
            if (want.MainRow != now.MainRow || want.MainIndex != now.MainIndex || want.MainFallback != now.MainFallback
                || want.RegularCount != now.RegularCount || want.MainCount != now.MainCount) failures.Add($"{now.Name}: main row {want.MainRow} -> {now.MainRow}");
            for (int i = 0; i < now.Samples.Count; i++)
                if (i >= want.Samples.Count || JsonSerializer.Serialize(want.Samples[i]) != JsonSerializer.Serialize(now.Samples[i])) failures.Add($"{now.Name}: sample {i}");
        }
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Golden { public string Format { get; set; } = ""; public List<BackendGolden> Backends { get; set; } = new(); }
    public sealed class BackendGolden
    {
        public string Name { get; set; } = ""; public string RecipeHash { get; set; } = ""; public double RowPhaseM { get; set; }
        public string NaturalIdentity { get; set; } = ""; public string RegistryIdentity { get; set; } = "";
        public int Land { get; set; } public int Island { get; set; } public int Mountain { get; set; }
        public int MainRow { get; set; } public int MainIndex { get; set; } public bool MainFallback { get; set; }
        public int RegularCount { get; set; } public int MainCount { get; set; }
        public List<SiteGolden> Samples { get; set; } = new();
    }
    public sealed class SiteGolden
    {
        public int Index { get; set; } public long Around { get; set; } public int Across { get; set; } public int Pad { get; set; }
        public float Beta { get; set; } public int Flags { get; set; } public string SiteHash { get; set; } = "";
    }
}
