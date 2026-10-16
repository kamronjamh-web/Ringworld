using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Spec V.1 climate_machine_registry_test for phase C1: count, offline split, spread and main-spire exclusion across many seeds, determinism across
/// threads and repeats, O(1) lookups against brute force (including the ring seam and both walls), recipe hash stability and the seed-1337 golden.
/// </summary>
public partial class ClimateMachineRegistryTest : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const string Format = "climate_machine_registry_v1";
    // Pinned recipe hash for seed 1337 (algorithm version 1); changing any recipe field or the algorithm version must change this deliberately.
    const string PinnedRecipeHash1337 = "B2329F352C046CFC";

    public int Run(bool capture)
        => RunGolden(ProjectSettings.GlobalizePath("res://tools/fixtures/climate_machine_registry_golden.json"), capture);

    public static int RunGolden(string fixture, bool capture)
    {
        if (capture && File.Exists(fixture)) return Fail("Refusing to overwrite existing golden fixture: " + fixture);
        var failures = new List<string>();
        try
        {
            var golden = new Golden { Format = Format };
            using (RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337))
            using (INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler))
            {
                RunReal("gen17_v4_seed_1337", 1337, source, golden, failures);
            }
            using (RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(7331))
            using (INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler))
            {
                RunReal("gen17_v4_seed_7331", 7331, source, null, failures);
            }
            RunRecipeChecks(failures);
            RunSyntheticSeeds(failures);
            if (capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(golden, JsonOptions) + System.Environment.NewLine);
                Console.WriteLine("CLIMATE_MACHINE_REGISTRY_CAPTURE=DONE");
            }
            else
            {
                if (!File.Exists(fixture)) failures.Add("golden fixture missing");
                else CompareGolden(JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions), golden, failures);
            }
        }
        catch (Exception exception) { return Fail(exception.ToString()); }
        if (failures.Count != 0) return Fail("CLIMATE_MACHINE_REGISTRY=FAIL " + string.Join(" | ", failures));
        Console.WriteLine("CLIMATE_MACHINE_REGISTRY=PASS");
        return 0;
    }

    static void RunReal(string name, long seed, INaturalHeightSource source, Golden? golden, List<string> failures)
    {
        EmitterSiteRecipe emitterRecipe = EmitterSiteRecipe.Default(seed);
        EmitterSiteRegistry emitters = EmitterSiteRegistry.GetOrBuild(emitterRecipe, source);
        ClimateMachineRecipe recipe = ClimateMachineRecipe.Default(seed);
        ClimateMachineRegistry.ClearCache();
        ClimateMachineRegistry registry = ClimateMachineRegistry.GetOrBuild(recipe, source, emitters);
        var warm = Stopwatch.StartNew();
        ClimateMachineRegistry again = ClimateMachineRegistry.GetOrBuild(recipe, source, emitters);
        warm.Stop();
        if (!ReferenceEquals(registry, again)) failures.Add(name + ": cache returned a different instance");
        Console.WriteLine($"CLIMATE_MACHINE_TIMING {name} cold_build_ms={registry.BuildMilliseconds:F0} threads={registry.BuildThreads} cached_lookup_ms={warm.Elapsed.TotalMilliseconds:F4}");

        byte[] reference = registry.ToCanonicalBytes();
        foreach (int threads in new[] { 1, 3, 8, 3 })
        {
            ClimateMachineRegistry other = ClimateMachineRegistry.Build(recipe, source, emitters, threads);
            if (!reference.AsSpan().SequenceEqual(other.ToCanonicalBytes())) failures.Add($"{name}: threads={threads} differs");
            if (other.Identity != registry.Identity) failures.Add($"{name}: threads={threads} identity differs");
            Console.WriteLine($"CLIMATE_MACHINE_TIMING {name} uncached_build_ms={other.BuildMilliseconds:F0} threads={threads}");
        }
        // Concurrent builds on separate threads agree too.
        var concurrent = new byte[4][];
        Parallel.For(0, 4, i => concurrent[i] = ClimateMachineRegistry.Build(recipe, source, emitters, 2).ToCanonicalBytes());
        foreach (byte[] bytes in concurrent) if (!reference.AsSpan().SequenceEqual(bytes)) failures.Add(name + ": concurrent build differs");

        CheckStructure(name, registry, failures);
        if (emitters.Identity != registry.EmitterIdentity || emitters.MainRow != registry.MainRow) failures.Add(name + ": emitter link mismatch");
        CheckLookups(name, registry, failures);
        PrintOffline(name, registry);
        Benchmark(name, registry);

        if (golden == null) return;
        var entry = new BackendGolden
        {
            Name = name, RecipeHash = recipe.Hash.ToString("X16"), NaturalIdentity = registry.NaturalIdentity.ToString("X16"),
            EmitterIdentity = registry.EmitterIdentity.ToString("X16"), RegistryIdentity = registry.Identity.ToString("X16"),
            MainRow = registry.MainRow, SelectionAttempt = registry.SelectionAttempt, MachineCount = registry.Count, OfflineCount = registry.OfflineIndices.Length,
            Storms = registry.Machines.ToArray().Count(m => m.Anomaly == ClimateAnomalyKind.Storm), Droughts = registry.Machines.ToArray().Count(m => m.Anomaly == ClimateAnomalyKind.Drought),
            Quay = registry.Machines.ToArray().Count(m => (m.Flags & ClimateMachineFlags.Quay) != 0),
        };
        foreach (int index in registry.OfflineIndices) entry.Offline.Add(ToGolden(registry[index], registry.AnomalyOf(index)));
        for (int i = 0; i < 8; i++) entry.Samples.Add(ToGolden(registry[i * 25 % registry.Count], null));
        golden.Backends.Add(entry);
    }

    static MachineGolden ToGolden(ClimateMachine m, ClimateAnomaly? a) => new()
    {
        Index = m.Index, Station = m.Station, Side = m.Side, Around = m.Around, Across = m.Across, Pad = m.PadHeight, Yaw = m.YawDegrees,
        Flags = (int)m.Flags, Kind = m.Anomaly.ToString(), MachineHash = m.MachineHash.ToString("X16"),
        SystemId = a?.SystemId ?? 0, CentreAcross = a?.CentreAcross ?? 0d,
    };

    /// <summary>Count, split, layout, spacing, exclusion and wall-side checks on a single registry.</summary>
    static void CheckStructure(string name, ClimateMachineRegistry r, List<string> failures)
    {
        ClimateMachineRecipe recipe = r.Recipe;
        if (r.Count != 200) failures.Add($"{name}: machine count {r.Count}");
        int storms = 0, droughts = 0;
        var stationOffline = new int[recipe.Stations];
        double pitch = recipe.RowPitchM;
        for (int i = 0; i < r.Count; i++)
        {
            ClimateMachine m = r[i];
            if (m.Index != i || m.Station != i / 2 || m.Side != i % 2) failures.Add($"{name}: machine {i} index/station/side");
            if (m.Side == 0 && (m.Across != 0 || m.YawDegrees != 180f)) failures.Add($"{name}: machine {i} west wall frame");
            if (m.Side == 1 && (m.Across != Width || m.YawDegrees != 0f)) failures.Add($"{name}: machine {i} east wall frame");
            // Yaw maps the asset forecourt (+Z) to world -across for the west wall and +across... world: local Z = -across.
            double yaw = m.YawDegrees * Math.PI / 180d;
            double worldZ = Math.Cos(yaw); // image of asset +Z (the forecourt direction) on world Z
            double acrossDirection = -worldZ;
            if (Math.Sign(acrossDirection) != (m.Side == 0 ? 1 : -1)) failures.Add($"{name}: machine {i} forecourt faces away from the floor");
            double nominal = recipe.RowPhaseM + (m.Station + 0.5d) * pitch;
            if (Math.Abs(m.Around - nominal) > 1.0001d) failures.Add($"{name}: machine {i} around {m.Around} vs mid-gap {nominal}");
            if (m.PadHeight < 8) failures.Add($"{name}: machine {i} pad {m.PadHeight}");
            if (m.IsOffline) { stationOffline[m.Station]++; if (m.Anomaly == ClimateAnomalyKind.Storm) storms++; else if (m.Anomaly == ClimateAnomalyKind.Drought) droughts++; else failures.Add($"{name}: machine {i} offline without a kind"); }
            else if (m.Anomaly != ClimateAnomalyKind.None) failures.Add($"{name}: online machine {i} has an anomaly");
        }
        if (r.OfflineIndices.Length != 6 || storms != 3 || droughts != 3) failures.Add($"{name}: offline {r.OfflineIndices.Length} storms {storms} droughts {droughts}");
        if (r.Anomalies.Length != 6) failures.Add($"{name}: anomaly count {r.Anomalies.Length}");
        foreach (int c in stationOffline) if (c > 1) failures.Add($"{name}: two offline machines in one station");
        var stations = r.Machines.ToArray().Where(m => m.IsOffline).Select(m => m.Station).ToArray();
        int minSpacing = int.MaxValue;
        for (int i = 0; i < stations.Length; i++)
            for (int j = i + 1; j < stations.Length; j++)
                minSpacing = Math.Min(minSpacing, ClimateMachineRegistry.CircularStationDistance(stations[i], stations[j], recipe.Stations));
        if (minSpacing < 10) failures.Add($"{name}: offline spacing {minSpacing}");
        double minMain = stations.Min(s => Math.Min(Math.Abs(s + 0.5 - r.MainRow), 100 - Math.Abs(s + 0.5 - r.MainRow)));
        if (minMain < 2d) failures.Add($"{name}: offline machine {minMain} rows from the main row");
        for (int i = 1; i < r.OfflineIndices.Length; i++) if (r.OfflineIndices[i] <= r.OfflineIndices[i - 1]) failures.Add(name + ": offline list not ascending");
        foreach (ClimateAnomaly a in r.Anomalies)
        {
            ClimateMachine m = r[a.MachineIndex];
            if (a.Kind != m.Anomaly || a.SystemId != -20000 - a.MachineIndex || a.CentreAround != m.Around) failures.Add($"{name}: anomaly {a.MachineIndex} record");
            bool storm = a.Kind == ClimateAnomalyKind.Storm;
            double inset = storm ? 70_000d : 90_000d;
            if (a.CentreAcross != (m.Side == 0 ? inset : Width - inset)) failures.Add($"{name}: anomaly {a.MachineIndex} centre across {a.CentreAcross}");
            if (a.RadiusAround != (storm ? 110_000d : 160_000d) || a.RadiusAcross != (storm ? 70_000d : 100_000d)) failures.Add($"{name}: anomaly {a.MachineIndex} radii");
        }
        Console.WriteLine($"CLIMATE_MACHINE_STRUCTURE {name} machines={r.Count} offline={r.OfflineIndices.Length} storms={storms} droughts={droughts} min_spacing_stations={minSpacing} min_main_distance_rows={minMain:F1} main_row={r.MainRow} attempt={r.SelectionAttempt} quay={r.Machines.ToArray().Count(m => (m.Flags & ClimateMachineFlags.Quay) != 0)} cutfill_conflict={r.Machines.ToArray().Count(m => (m.Flags & ClimateMachineFlags.CutFillConflict) != 0)}");
    }

    static void PrintOffline(string name, ClimateMachineRegistry r)
    {
        foreach (int index in r.OfflineIndices)
        {
            ClimateMachine m = r[index];
            ClimateAnomaly a = r.AnomalyOf(index);
            Console.WriteLine($"CLIMATE_MACHINE_OFFLINE {name} machine={index} station={m.Station} side={(m.Side == 0 ? "west" : "east")} kind={m.Anomaly} around={m.Around} across={m.Across} pad_m={m.PadHeight} flags={m.Flags} system_id={a.SystemId} centre_across={a.CentreAcross:F0} rows_from_main={Math.Min(Math.Abs(m.Station + 0.5 - r.MainRow), 100 - Math.Abs(m.Station + 0.5 - r.MainRow)):F1}");
        }
    }

    static double WrappedDelta(long a, long b)
    {
        long d = Math.Abs(a - b) % Circumference;
        return Math.Min(d, Circumference - d);
    }

    /// <summary>Fast lookups against brute force, including negative/multi-turn around values, the ring seam, both walls and the corners.</summary>
    static void CheckLookups(string name, ClimateMachineRegistry r, List<string> failures)
    {
        var rng = new Random(2024);
        long influenceChecks = 0, influenced = 0, nearestChecks = 0, radiusChecks = 0, anomalyChecks = 0;
        var points = new List<(double, double)>();
        for (int i = 0; i < 400_000; i++)
            points.Add((rng.NextDouble() * Circumference * 3d - Circumference, rng.NextDouble() * (Width + 2000) - 1000d));
        // Around every machine: inside, on and outside the influence boundary, with 0.5 m fractional around values.
        for (int i = 0; i < r.Count; i++)
        {
            ClimateMachine m = r[i];
            double reach = m.InfluenceRadius;
            double[] scales = { 0.0, 0.5, 0.999, 0.9999, 1.0, 1.0001, 1.001, 1.5 };
            for (int k = 0; k < 120; k++)
            {
                double angle = rng.NextDouble() * Math.Tau, radius = (reach + 10_000d) * scales[k % scales.Length];
                points.Add((m.Around + radius * Math.Cos(angle) + (k % 2) * 0.5d, m.Across + radius * Math.Sin(angle)));
            }
            // Along the footprint segment ends and the wall itself.
            foreach (double along in new[] { -10_001d, -10_000d, -9_999d, 9_999d, 10_000d, 10_001d, 0d })
                foreach (double depth in new[] { -5d, 0d, 1d, reach - 1d, reach, reach + 1d })
                    points.Add((m.Around + along, m.Side == 0 ? depth : Width - depth));
        }
        // Ring seam, walls, corners, mid-ring.
        double[] seams = { 0, 0.5, 1, 2, Circumference - 1, Circumference - 0.5, Circumference, Circumference + 1, -1, -Circumference, Circumference * 2 };
        double[] edges = { -10, 0, 1, 1000, 8000, 8399, 8400, 8401, 100_000, 158_999, 159_000, 159_001, 309_600, 309_601, Width - 1000, Width - 1, Width, Width + 10 };
        foreach (double a in seams) foreach (double x in edges) points.Add((a, x));
        // Between stations at every gap boundary.
        for (int g = 0; g < 100; g++)
            foreach (double e in new[] { -2d, -0.5d, 0d, 0.5d, 2d })
                foreach (double x in new[] { 0d, 5000d, 159_000d, Width - 5000d, Width })
                    points.Add((r.Recipe.RowPhaseM + g * r.Recipe.RowPitchM + e, x));

        foreach ((double around, double across) in points)
        {
            influenceChecks++;
            bool fast = r.TryGetInfluencingMachine(around, across, out int index, out double du, out double dw);
            int brute = r.FindInfluencingMachineBruteForce(around, across, out int matches);
            if (matches > 1) { failures.Add($"{name}: {matches} machines influence ({around},{across})"); break; }
            if (fast != (brute >= 0) || (fast && index != brute)) { failures.Add($"{name}: influence mismatch at ({around},{across}) fast={fast}/{index} brute={brute}"); break; }
            if (fast)
            {
                influenced++;
                if (Math.Abs(du) > Circumference / 2 || index % 2 != (across < Width / 2d ? 0 : 1)) { failures.Add($"{name}: bad du or wall side at ({around},{across})"); break; }
                if (r[index].Side == 0 && across > 8_400d + 1 || r[index].Side == 1 && across < Width - 8_401d) { failures.Add($"{name}: influence wrong wall at ({around},{across})"); break; }
            }
        }

        foreach (ClimateMachineFilter filter in new[] { ClimateMachineFilter.Any, ClimateMachineFilter.Offline, ClimateMachineFilter.Storm, ClimateMachineFilter.Drought })
        {
            int bad = 0;
            for (int p = 0; p < points.Count && bad == 0; p += 3)
            {
                (double around, double across) = points[p];
                nearestChecks++;
                bool ok = r.TryGetNearestMachine(around, across, filter, out int index, out double du, out double dw);
                int want = r.FindNearestMachineBruteForce(around, across, filter);
                if (!ok || index != want || Math.Abs(du) > Circumference / 2)
                { failures.Add($"{name}: nearest({filter}) mismatch at ({around},{across}) got {index} want {want}"); bad++; }
                else if (Math.Abs(du * du + dw * dw - Math.Pow(Math.Sqrt(Math.Pow(WrappedDelta((long)Math.Floor(((around % Circumference) + Circumference) % Circumference), r[index].Around), 2) + Math.Pow(across - r[index].Across, 2)), 2)) > 4d + Math.Abs(du) * 2d)
                { failures.Add($"{name}: nearest du/dw inconsistent at ({around},{across})"); bad++; }
            }
        }

        Span<int> found = stackalloc int[r.Count];
        double[] radii = { 0d, 500d, 20_000d, 160_000d, 330_000d, 700_000d, 5_000_000d, 40_000_000d };
        int radiusBad = 0;
        for (int p = 0; p < points.Count && radiusBad == 0; p += 11)
        {
            (double around, double across) = points[p];
            double radius = radii[(p / 11) % radii.Length];
            radiusChecks++;
            int count = r.GetMachinesWithinRadius(around, across, radius, found);
            double wrapped = ((around % Circumference) + Circumference) % Circumference;
            int expected = 0;
            for (int i = 0; i < r.Count; i++)
            {
                double dx = wrapped - r[i].Around; double half = Circumference / 2d;
                if (dx > half) dx -= Circumference; else if (dx < -half) dx += Circumference;
                double dy = across - r[i].Across;
                if (dx * dx + dy * dy > radius * radius) continue;
                if (expected >= count || found[expected] != i) { radiusBad++; break; }
                expected++;
            }
            if (expected != count && radiusBad == 0) radiusBad++;
            if (radiusBad != 0) failures.Add($"{name}: radius enumeration mismatch at ({around},{across}) r={radius} got {count} want {expected}");
        }
        if (r.GetMachinesWithinRadius(0d, 159_000d, 2_000_000d, stackalloc int[4]) < 4) failures.Add(name + ": truncated enumeration count");

        // Anomaly weights: bounded, 1 at the centre, 0 outside, continuous, dominant agrees with the per-kind maxima.
        foreach (ClimateAnomaly a in r.Anomalies)
        {
            r.EvaluateAnomalies(a.CentreAround, a.CentreAcross, out double s, out double d);
            double own = a.Kind == ClimateAnomalyKind.Storm ? s : d;
            if (own != 1d) failures.Add($"{name}: anomaly {a.MachineIndex} centre weight {own}");
            if (!r.TryGetDominantAnomaly(a.CentreAround, a.CentreAcross, out ClimateAnomaly dom, out double dw0) || dom.MachineIndex != a.MachineIndex || dw0 != 1d)
                failures.Add($"{name}: dominant anomaly at centre of {a.MachineIndex}");
            double prev = 1d;
            for (int k = 0; k <= 2000; k++)
            {
                double ra = a.RadiusAround * k / 2000d * 1.1d;
                r.EvaluateAnomalies(a.CentreAround + ra, a.CentreAcross, out double s2, out double d2);
                double w = a.Kind == ClimateAnomalyKind.Storm ? s2 : d2;
                anomalyChecks++;
                if (w < 0d || w > 1d || w > prev + 1e-12 || prev - w > 0.02) { failures.Add($"{name}: anomaly {a.MachineIndex} weight profile at step {k}: {w} after {prev}"); break; }
                prev = w;
            }
            // Across the ring seam: shift the query by a full circumference.
            r.EvaluateAnomalies(a.CentreAround + Circumference * 2 - 5000d, a.CentreAcross + 3000d, out double s3, out double d3);
            r.EvaluateAnomalies(a.CentreAround - 5000d, a.CentreAcross + 3000d, out double s4, out double d4);
            if (s3 != s4 || d3 != d4) failures.Add($"{name}: anomaly weight not periodic around the ring");
        }
        r.EvaluateAnomalies(r[0].Around, 159_000d, out double sFar, out double dFar);
        Console.WriteLine($"CLIMATE_MACHINE_LOOKUP {name} influence_points={influenceChecks} influenced={influenced} nearest_checks={nearestChecks} radius_checks={radiusChecks} anomaly_steps={anomalyChecks} (all equal to brute force: {failures.Count == 0})");
    }

    static void Benchmark(string name, ClimateMachineRegistry r)
    {
        const int Count = 4_000_000;
        var rng = new Random(5);
        double[] around = new double[4096], across = new double[4096];
        for (int i = 0; i < around.Length; i++) { around[i] = rng.NextDouble() * Circumference; across[i] = rng.NextDouble() * Width; }
        int sink = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Count; i++) sink += r.TryGetInfluencingMachine(around[i & 4095], across[i & 4095], out int idx, out _, out _) ? idx : 1;
        watch.Stop();
        double influenceNs = watch.Elapsed.TotalMilliseconds * 1e6 / Count;
        watch.Restart();
        for (int i = 0; i < Count; i++) sink += r.TryGetNearestMachine(around[i & 4095], across[i & 4095], ClimateMachineFilter.Any, out int idx, out _, out _) ? idx : 1;
        watch.Stop();
        double nearestNs = watch.Elapsed.TotalMilliseconds * 1e6 / Count;
        watch.Restart();
        double acc = 0d;
        for (int i = 0; i < Count; i++) { r.EvaluateAnomalies(around[i & 4095], across[i & 4095], out double s, out double d); acc += s + d; }
        watch.Stop();
        Console.WriteLine($"CLIMATE_MACHINE_PERF {name} influence_ns={influenceNs:F1} nearest_ns={nearestNs:F1} anomaly_eval_ns={watch.Elapsed.TotalMilliseconds * 1e6 / Count:F1} ops={Count} sink={sink}/{acc:F1}");
    }

    static void RunRecipeChecks(List<string> failures)
    {
        ClimateMachineRecipe a = ClimateMachineRecipe.Default(1337), a2 = ClimateMachineRecipe.Default(1337);
        ClimateMachineRecipe b = ClimateMachineRecipe.Default(7331), big = ClimateMachineRecipe.Default(0x1_0000_0539L);
        if (a.Hash != a2.Hash || a.RowPhaseM != a2.RowPhaseM) failures.Add("climate recipe not deterministic for equal seeds");
        if (a.Hash == b.Hash || a.Hash == big.Hash || b.Hash == big.Hash) failures.Add("climate recipe hash does not depend on the seed");
        if (a.RowPhaseM != EmitterSiteRecipe.Default(1337).RowPhaseM || a.RowPitchM != EmitterSiteRecipe.Default(1337).RowPitchM) failures.Add("climate recipe is not locked to the emitter pitch and phase");
        if (a.AlgorithmVersion != 1 || a.MachineCount != 200 || a.OfflineCount != 6) failures.Add("climate recipe version/count/offline");
        if (a.InfluenceRadiusM > 9_000d) failures.Add($"climate influence radius {a.InfluenceRadiusM} > 9 km");
        if (PinnedRecipeHash1337.Length != 0 && a.Hash.ToString("X16") != PinnedRecipeHash1337) failures.Add($"climate recipe hash {a.Hash:X16} != pinned {PinnedRecipeHash1337}");
        Console.WriteLine($"CLIMATE_MACHINE_RECIPE hash_1337={a.Hash:X16} hash_7331={b.Hash:X16} hash_2p32+1337={big.Hash:X16} influence_km={a.InfluenceRadiusM / 1000d:F1}");
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

    static SyntheticSource SyntheticWorld(long seed)
    {
        double p1 = (seed % 977) * 0.013, p2 = (seed % 541) * 0.021;
        return new SyntheticSource((a, x) => 60d + 220d * Math.Sin(a * 3.1e-6 + p1) * Math.Cos(x * 2.3e-5 + p2) + 90d * Math.Sin(a * 4.7e-5 + x * 1.1e-5), 0x5EED0000UL + (ulong)seed);
    }

    /// <summary>Many-seed spread: offline count/split, spacing, main exclusion, thread determinism; prints the distribution.</summary>
    static void RunSyntheticSeeds(List<string> failures)
    {
        const int Seeds = 400;
        var attempts = new int[8];
        int minSpacing = int.MaxValue, minMain = int.MaxValue, badSplit = 0, quayMachines = 0;
        double minMainRows = double.MaxValue;
        var stationHist = new int[10];
        var sideCounts = new int[2];
        var stormPerSide = new int[2];
        var droughtPerSide = new int[2];
        var spacingHist = new SortedDictionary<int, int>();
        var rowsFromMainHist = new SortedDictionary<int, int>();
        var kindsByLandRank = new int[2];
        for (long seed = 1; seed <= Seeds; seed++)
        {
            long s = seed == Seeds ? 0x1_0000_0539L : seed;
            using SyntheticSource world = SyntheticWorld(s);
            ClimateMachineRecipe recipe = ClimateMachineRecipe.Default(s);
            int mainRow = (int)(EmitterSiteHash.Derive(s, "test-main-row", 0) % 100UL);
            ClimateMachineRegistry reg = ClimateMachineRegistry.BuildWithMainRow(recipe, world, mainRow, 4);
            if (seed % 25 == 0)
            {
                byte[] reference = reg.ToCanonicalBytes();
                foreach (int threads in new[] { 1, 3, 8 })
                    if (!reference.AsSpan().SequenceEqual(ClimateMachineRegistry.BuildWithMainRow(recipe, world, mainRow, threads).ToCanonicalBytes())) failures.Add($"synthetic seed {s}: threads={threads} differs");
            }
            int offline = reg.OfflineIndices.Length;
            int storms = reg.Anomalies.ToArray().Count(a => a.Kind == ClimateAnomalyKind.Storm), droughts = reg.Anomalies.Length - storms;
            if (reg.Count != 200 || offline != 6 || storms != 3 || droughts != 3) { badSplit++; failures.Add($"synthetic seed {s}: count {reg.Count} offline {offline} {storms}/{droughts}"); }
            attempts[Math.Min(7, reg.SelectionAttempt)]++;
            quayMachines += reg.Machines.ToArray().Count(m => (m.Flags & ClimateMachineFlags.Quay) != 0);
            var st = reg.Machines.ToArray().Where(m => m.IsOffline).ToArray();
            for (int i = 0; i < st.Length; i++)
            {
                sideCounts[st[i].Side]++;
                if (st[i].Anomaly == ClimateAnomalyKind.Storm) stormPerSide[st[i].Side]++; else droughtPerSide[st[i].Side]++;
                stationHist[st[i].Station / 10]++;
                double rows = Math.Abs(st[i].Station + 0.5 - mainRow); rows = Math.Min(rows, 100 - rows);
                minMainRows = Math.Min(minMainRows, rows);
                int bucket = (int)(rows / 5) * 5; rowsFromMainHist[bucket] = rowsFromMainHist.GetValueOrDefault(bucket) + 1;
                for (int j = i + 1; j < st.Length; j++)
                {
                    int d = ClimateMachineRegistry.CircularStationDistance(st[i].Station, st[j].Station, 100);
                    minSpacing = Math.Min(minSpacing, d);
                    int sb = d / 5 * 5; spacingHist[sb] = spacingHist.GetValueOrDefault(sb) + 1;
                }
            }
            minMain = Math.Min(minMain, (int)Math.Floor(minMainRows));
            if (minSpacing < 10) { failures.Add($"synthetic seed {s}: spacing {minSpacing}"); break; }
            if (minMainRows < 2d) { failures.Add($"synthetic seed {s}: main exclusion {minMainRows}"); break; }
        }
        Console.WriteLine($"CLIMATE_MACHINE_SEEDS seeds={Seeds} bad_split={badSplit} min_spacing_stations={minSpacing} min_rows_from_main={minMainRows:F1} selection_attempts[0..7+]={string.Join(",", attempts)}");
        Console.WriteLine($"CLIMATE_MACHINE_SEEDS offline_by_station_decile(0-9..90-99)={string.Join(",", stationHist)} offline_per_wall west={sideCounts[0]} east={sideCounts[1]} storms west/east={stormPerSide[0]}/{stormPerSide[1]} droughts west/east={droughtPerSide[0]}/{droughtPerSide[1]} quay_machines_total={quayMachines}");
        Console.WriteLine($"CLIMATE_MACHINE_SEEDS pair_spacing_hist(stations,bucket5)={string.Join(" ", spacingHist.Select(kv => kv.Key + ":" + kv.Value))} rows_from_main_hist(bucket5)={string.Join(" ", rowsFromMainHist.Select(kv => kv.Key + ":" + kv.Value))}");
        if (attempts.Skip(1).Sum() * 20 > Seeds) failures.Add($"offline selection needed re-ranking in {attempts.Skip(1).Sum()} of {Seeds} seeds");
        // Different seeds give different offline sets.
        var distinct = new HashSet<string>();
        for (long seed = 1; seed <= 40; seed++)
        {
            using SyntheticSource world = SyntheticWorld(seed);
            distinct.Add(string.Join(",", ClimateMachineRegistry.BuildWithMainRow(ClimateMachineRecipe.Default(seed), world, 40).OfflineIndices.ToArray()));
        }
        if (distinct.Count < 38) failures.Add($"offline sets take only {distinct.Count} values over 40 seeds");
        Console.WriteLine($"CLIMATE_MACHINE_SEEDS distinct_offline_sets_over_40_seeds={distinct.Count}");
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
            if (want.NaturalIdentity != now.NaturalIdentity) failures.Add(now.Name + ": natural identity");
            if (want.EmitterIdentity != now.EmitterIdentity) failures.Add(now.Name + ": emitter identity");
            if (want.RegistryIdentity != now.RegistryIdentity) failures.Add(now.Name + ": registry identity " + want.RegistryIdentity + " -> " + now.RegistryIdentity);
            if (want.MainRow != now.MainRow || want.SelectionAttempt != now.SelectionAttempt || want.MachineCount != now.MachineCount || want.OfflineCount != now.OfflineCount
                || want.Storms != now.Storms || want.Droughts != now.Droughts || want.Quay != now.Quay) failures.Add(now.Name + ": counts");
            if (JsonSerializer.Serialize(want.Offline) != JsonSerializer.Serialize(now.Offline)) failures.Add(now.Name + ": offline list");
            if (JsonSerializer.Serialize(want.Samples) != JsonSerializer.Serialize(now.Samples)) failures.Add(now.Name + ": samples");
        }
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Golden { public string Format { get; set; } = ""; public List<BackendGolden> Backends { get; set; } = new(); }
    public sealed class BackendGolden
    {
        public string Name { get; set; } = ""; public string RecipeHash { get; set; } = ""; public string NaturalIdentity { get; set; } = "";
        public string EmitterIdentity { get; set; } = ""; public string RegistryIdentity { get; set; } = "";
        public int MainRow { get; set; } public int SelectionAttempt { get; set; } public int MachineCount { get; set; } public int OfflineCount { get; set; }
        public int Storms { get; set; } public int Droughts { get; set; } public int Quay { get; set; }
        public List<MachineGolden> Offline { get; set; } = new(); public List<MachineGolden> Samples { get; set; } = new();
    }
    public sealed class MachineGolden
    {
        public int Index { get; set; } public int Station { get; set; } public int Side { get; set; } public long Around { get; set; } public int Across { get; set; }
        public int Pad { get; set; } public float Yaw { get; set; } public int Flags { get; set; } public string Kind { get; set; } = "";
        public string MachineHash { get; set; } = ""; public int SystemId { get; set; } public double CentreAcross { get; set; }
    }
}
