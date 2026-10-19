using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Recipe v7 (ringworld_surface_landform_v7) checks from docs/lowland-landform-overhaul-plan.md section 9. Sections: "recipe" (parameters,
/// ranges, flags, hashes, registry gating) and "shape" (formula calibration and invariants).
/// </summary>
public partial class LandformTerrainV7Test : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;

    // name, minimum, maximum, isInteger. Bounds that depend on another field are covered by CheckCrossConstraints.
    static readonly (string Name, double Min, double Max, bool IsInt)[] StaticRanges =
    {
        ("climate_machines", 0, 1, true), ("landform_scale_m", 8000, 200000, false), ("plains_threshold", .05, .95, false), ("plateau_scale_m", 16000, 400000, false),
        ("plateau_threshold", .05, .95, false), ("hill_amplitude_m", 0, 900, false), ("hill_scale_m", 1000, 16000, false), ("hill_detail_weight", 0, 1, false),
        ("hill_valley_exponent", .5, 3, false), ("plains_hill_fraction", 0, .5, false), ("hill_valley_floor", 0, 1, false), ("plateau_lift_m", 0, 900, false),
        ("plateau_step_m", 20, 600, false), ("terrace_riser_width", .04, .5, false), ("terrace_strength", 0, 1, false), ("escarpment_jitter_m", 0, 300, false),
        ("escarpment_scale_m", 2000, 50000, false), ("breaks_strength", 0, 1, false), ("breaks_step_m", 8, 200, false), ("landform_shore_start_m", 0, 500, false),
        ("lowland_detail_floor", 0, 1, false), ("plains_detail_fraction", 0, 1, false), ("lowland_erosion_strength_m", 0, 120, false),
    };

    public int Run(string section)
    {
        var failures = new List<string>();
        try
        {
            if (section == "recipe" || section == "all") Recipe(failures);
            if (section == "shape" || section == "all") foreach (int seed in new[] { 1337, 90210 }) Shape(seed, failures);
            if (section == "gullies" || section == "all") foreach (int seed in new[] { 1337, 90210 }) Gullies(seed, failures);
            if (section == "world" || section == "all") World(1337, failures);
            if (section == "lodgap") LodGap(1337);
            if (section == "locate") LocateBenchmarkTiles(1337);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("LANDFORM_V7_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? $"LANDFORM_V7_{section.ToUpperInvariant()}=PASS" : $"LANDFORM_V7_{section.ToUpperInvariant()}=FAIL failures={failures.Count}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Canonical JSON of the C# v7 default (seed 1337), for the GDScript recipe comparison.</summary>
    public string LandformDefaultCanonicalJson() => TerrainGenerationParameters.LandformDefault().CanonicalJson;

    // ---- recipe ------------------------------------------------------------------------------------------------

    static string WithField(string json, string name, string value)
    {
        var regex = new Regex("\"" + Regex.Escape(name) + "\":[^,}]+");
        if (!regex.IsMatch(json)) throw new InvalidOperationException("field not in json: " + name);
        return regex.Replace(json, "\"" + name + "\":" + value, 1);
    }

    static bool Rejects(string json)
    {
        try { TerrainGenerationParameters.FromJson(json); return false; }
        catch (Exception) { return true; }
    }

    static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    static void Recipe(List<string> failures)
    {
        TerrainGenerationParameters v7 = TerrainGenerationParameters.LandformDefault(), v6 = TerrainGenerationParameters.ClimateDefault(), v5 = TerrainGenerationParameters.SitesDefault();
        string json = v7.CanonicalJson;

        // Canonical round trip and field-set shape.
        TerrainGenerationParameters again = TerrainGenerationParameters.FromJson(json);
        if (again.CanonicalJson != json || again.DefinitionHash != v7.DefinitionHash) failures.Add("v7 canonical json does not round trip");
        if (!json.StartsWith(v6.CanonicalJson.Replace("ringworld_surface_climate_v6", "ringworld_surface_landform_v7")[..^1] + ",\"climate_machines\":0,\"landform_scale_m\":32000,"))
            failures.Add("v7 canonical json is not the v6 segment followed by the v7 segment");
        if (!json.EndsWith("\"lowland_erosion_strength_m\":24,\"lowland_erosion_octaves\":2}")) failures.Add("v7 canonical segment order changed");
        if (v6.CanonicalJson.Contains("climate_machines") || v5.CanonicalJson.Contains("\"hill_amplitude_m\"")) failures.Add("v5/v6 canonical json carries v7 fields");
        if (!Rejects(v6.CanonicalJson.Replace("climate_v6", "landform_v7"))) failures.Add("v7 id accepted a v6 field set");
        if (!Rejects(json.Replace("}", ",\"extra_field\":1}"))) failures.Add("v7 accepted an unknown field");
        if (!Rejects(json.Replace(",\"hill_scale_m\":5000", ""))) failures.Add("v7 accepted a missing field");
        if (!Rejects(v6.CanonicalJson.Replace("}", ",\"hill_scale_m\":5000}"))) failures.Add("v6 accepted a v7 field");

        // Flags.
        if (!v7.HasLowlandLandforms || !v7.HasMountainDetail || !v7.HasEnhancedErosion || !v7.HasEmitterSites) failures.Add("v7 feature flags");
        if (v7.HasClimateMachines) failures.Add("v7 default has climate machines");
        if (v6.HasLowlandLandforms || v5.HasLowlandLandforms || !v6.HasClimateMachines || v5.HasClimateMachines) failures.Add("v5/v6 feature flags changed");
        TerrainGenerationParameters v7c = TerrainGenerationParameters.FromJson(WithField(json, "climate_machines", "1"));
        if (!v7c.HasClimateMachines || !v7c.HasLowlandLandforms) failures.Add("v7 with climate_machines=1 flags");

        // Range rejection: just outside every bound, plus values just inside (the bound itself) accepted.
        int rejected = 0, accepted = 0;
        foreach ((string name, double min, double max, bool isInt) in StaticRanges)
        {
            double eps = isInt ? 1 : Math.Max(1e-6, Math.Abs(max - min) * 1e-6);
            if (Rejects(WithField(json, name, Num(min - eps)))) rejected++; else failures.Add($"{name} accepted below its minimum");
            if (Rejects(WithField(json, name, Num(max + eps)))) rejected++; else failures.Add($"{name} accepted above its maximum");
            // The upper bounds of fields that other bounds depend on are covered by the cross-constraint checks below.
            bool dependent = name is "plains_threshold" or "plateau_threshold" or "landform_shore_start_m";
            try { TerrainGenerationParameters.FromJson(WithField(json, name, Num(min))); accepted++; if (!dependent) { TerrainGenerationParameters.FromJson(WithField(json, name, Num(max))); accepted++; } }
            catch (Exception exception) { failures.Add($"{name} rejected its own bound: {exception.Message}"); }
        }
        if (!Rejects(WithField(json, "climate_machines", "0.5"))) failures.Add("climate_machines accepted a non-integer");
        if (!Rejects(WithField(json, "lowland_erosion_octaves", "1.5"))) failures.Add("lowland_erosion_octaves accepted a non-integer");
        if (!Rejects(WithField(json, "lowland_erosion_octaves", "0"))) failures.Add("lowland_erosion_octaves accepted 0");
        if (!Rejects(WithField(json, "lowland_erosion_octaves", "4"))) failures.Add("lowland_erosion_octaves accepted more than erosion_octaves (3)");
        try { TerrainGenerationParameters.FromJson(WithField(json, "lowland_erosion_octaves", "3")); } catch (Exception exception) { failures.Add("lowland_erosion_octaves rejected erosion_octaves: " + exception.Message); }

        // Cross-field constraints.
        (string, string)[][] crossRejects =
        {
            new[] { ("hills_threshold", "0.445"), ("hills_threshold", "0.4549"), ("hills_threshold", "0.991") },
            new[] { ("plateau_full_threshold", "0.58"), ("plateau_full_threshold", "0.5899"), ("plateau_full_threshold", "0.991") },
            new[] { ("landform_shore_full_m", "20"), ("landform_shore_full_m", "20.5"), ("landform_shore_full_m", "1500.5") },
        };
        foreach ((string name, string value) in crossRejects.SelectMany(group => group))
            if (!Rejects(WithField(json, name, value))) failures.Add($"{name}={value} accepted despite the cross constraint");
        // Raising plains_threshold above the default hills_threshold-0.01 must fail unless hills_threshold moves too.
        if (!Rejects(WithField(json, "plains_threshold", "0.57"))) failures.Add("plains_threshold 0.57 accepted with hills_threshold 0.575");
        try { TerrainGenerationParameters.FromJson(WithField(WithField(json, "plains_threshold", "0.9"), "hills_threshold", "0.91")); } catch (Exception exception) { failures.Add("valid moved thresholds rejected: " + exception.Message); }
        if (!Rejects(WithField(json, "landform_shore_start_m", "500"))) failures.Add("shore start 500 accepted with shore full 140");
        try { TerrainGenerationParameters.FromJson(WithField(WithField(json, "landform_shore_start_m", "500"), "landform_shore_full_m", "1500")); } catch (Exception exception) { failures.Add("valid moved shore buffer rejected: " + exception.Message); }

        // Hashes and registry gating through the real sampler.
        TerrainGenerationParameters p6 = EmitterSiteTestWorlds.Gen17ClimateRecipe(1337), p7 = EmitterSiteTestWorlds.Gen17LandformRecipe(1337), p7c = EmitterSiteTestWorlds.Gen17LandformRecipe(1337, true);
        if (p7.DefinitionHash == p6.DefinitionHash || p7.DefinitionHash == p7c.DefinitionHash || p7c.DefinitionHash == p6.DefinitionHash) failures.Add("recipe hashes are not distinct across v6, v7 and v7+climate");
        if (EmitterSiteTestWorlds.Gen17LandformRecipe(1338).DefinitionHash == p7.DefinitionHash) failures.Add("v7 hash ignores the seed");
        using RingTerrainSampler s6 = EmitterSiteTestWorlds.CreateGen17Climate(1337);
        using RingTerrainSampler s7 = EmitterSiteTestWorlds.CreateGen17Landform(1337);
        using RingTerrainSampler s7c = EmitterSiteTestWorlds.CreateGen17Landform(1337, true);
        if (s7.TerrainGeneratorVersion != 17 || s7c.TerrainGeneratorVersion != 17) failures.Add("generator version must stay 17");
        if (s7.TerrainDefinitionHash == s6.TerrainDefinitionHash || s7.TerrainDefinitionHash == s7c.TerrainDefinitionHash || s7c.TerrainDefinitionHash == s6.TerrainDefinitionHash) failures.Add("sampler definition hashes are not distinct");
        if (s7.ClimateMachines != null) failures.Add("v7 with climate_machines=0 exposes a climate registry");
        if (s7c.ClimateMachines == null || s7c.ClimateMachines.Count != 200) failures.Add("v7 with climate_machines=1 lacks the 200-machine climate registry");
        if (s7.EmitterSites == null || s7c.EmitterSites == null || s7.EmitterSites.Count == 0) failures.Add("v7 lacks the emitter-site registry");
        if (!s7.SurfaceGenerationRecipeJson.Contains("ringworld_surface_landform_v7")) failures.Add("v7 id missing from the sampler recipe json");
        Console.WriteLine($"LANDFORM_V7_RECIPE ranges_rejected={rejected} bounds_accepted={accepted} v6={p6.DefinitionHash:X16} v7={p7.DefinitionHash:X16} v7_climate={p7c.DefinitionHash:X16} sampler_v7={s7.TerrainDefinitionHash:X16} sampler_v7_climate={s7c.TerrainDefinitionHash:X16}");
    }

    // ---- world -------------------------------------------------------------------------------------------------

    /// <summary>Plan Step 7 "world": real v7 samplers with and without climate machines. Registries, cut/fill caps, viewpoints, cold build time, biome drift and worker copies.</summary>
    static void World(int seed, List<string> failures)
    {
        EmitterSiteRegistry.ClearCache(); ClimateMachineRegistry.ClearCache(); EmitterSiteTerrainShaper.ClearCache();
        var sw = Stopwatch.StartNew();
        using RingTerrainSampler s6 = EmitterSiteTestWorlds.CreateGen17Climate(seed);
        double v6ColdMs = sw.Elapsed.TotalMilliseconds;
        EmitterSiteRegistry.ClearCache(); ClimateMachineRegistry.ClearCache(); EmitterSiteTerrainShaper.ClearCache();
        sw.Restart();
        using RingTerrainSampler s7 = EmitterSiteTestWorlds.CreateGen17Landform(seed);
        double v7ColdMs = sw.Elapsed.TotalMilliseconds;
        EmitterSiteRegistry.ClearCache(); ClimateMachineRegistry.ClearCache(); EmitterSiteTerrainShaper.ClearCache();
        sw.Restart();
        using RingTerrainSampler s7c = EmitterSiteTestWorlds.CreateGen17Landform(seed, true);
        double v7cColdMs = sw.Elapsed.TotalMilliseconds;
        EmitterSiteRegistry sites = s7c.EmitterSites ?? throw new InvalidOperationException("v7 sampler has no emitter registry");
        ClimateMachineRegistry climate = s7c.ClimateMachines ?? throw new InvalidOperationException("v7+climate sampler has no climate registry");
        Console.WriteLine($"LANDFORM_V7_WORLD_BUILD sampler_ctor_cold_ms v6={v6ColdMs:F0} v7={v7ColdMs:F0} v7_climate={v7cColdMs:F0} emitter_sites={sites.Count} climate_machines={climate.Count} climate_registry_build_ms={climate.BuildMilliseconds:F0}");
        if (sites.Count == 0 || climate.Count != 200) failures.Add($"registries incomplete: sites={sites.Count} machines={climate.Count}");

        // Cut/fill caps, pad flatness and operator agreement through the existing v5/v6 checks, run against the v7 sampler and v7 natural height model.
        using var natural = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17LandformRecipe(seed, true), false);
        int before = failures.Count;
        EmitterSiteTerrainV5Test.CheckOperatorAgreement(s7c, sites, natural, failures);
        EmitterSiteTerrainV5Test.CheckPadFlat(s7c, sites, failures);
        ClimateTerrainV6Test.CheckForecourts(s7c, climate, sites, natural, failures);
        Console.WriteLine($"LANDFORM_V7_WORLD_SHAPING new_failures={failures.Count - before}");

        // Every emitter viewpoint resolves against the v7 ground.
        var nav = new EmitterSiteNavigator(new EmitterSitePlacementSource(sites), Circumference / Math.Tau - 50d, 0d);
        Func<double, double, float> ground = (a, x) => s7c.GetPresentationSurfaceHeight(a, x);
        int poses = 0, badPoses = 0;
        for (int i = 0; i < nav.Count; i++)
            foreach (EmitterViewpoint vp in Enum.GetValues<EmitterViewpoint>())
            {
                EmitterViewpointPose pose = nav.GetPose(i, vp, ground, 20d, 150d);
                poses++;
                if (!double.IsFinite(pose.Around) || !double.IsFinite(pose.Vertical) || !double.IsFinite(pose.Across) || !double.IsFinite(pose.YawDeg) || !double.IsFinite(pose.PitchDeg)) badPoses++;
            }
        Console.WriteLine($"LANDFORM_V7_WORLD_VIEWPOINTS sites={nav.Count} poses={poses} non_finite={badPoses}");
        if (badPoses != 0) failures.Add($"{badPoses} emitter viewpoints did not resolve");

        // Primary-biome drift v6 -> v7 on a fixed random sample of the ring.
        var rng = new Random(777);
        const int N = 20000;
        var pts = new (long A, int X)[N];
        for (int i = 0; i < N; i++) pts[i] = ((long)(rng.NextDouble() * Circumference), (int)(rng.NextDouble() * Width));
        int changed = 0, landPts = 0, changedLand = 0;
        for (int i = 0; i < N; i++)
        {
            TerrainSample a = s6.GetTerrainSample(pts[i].A, pts[i].X), b = s7.GetTerrainSample(pts[i].A, pts[i].X);
            if (a.Biome != b.Biome) changed++;
            if (a.SurfaceHeight > 0 && b.SurfaceHeight > 0) { landPts++; if (a.Biome != b.Biome) changedLand++; }
        }
        double frac = changed / (double)N, fracLand = landPts == 0 ? 0d : changedLand / (double)landPts;
        Console.WriteLine($"LANDFORM_V7_WORLD_BIOME points={N} changed={frac:P2} land_points={landPts} land_changed={fracLand:P2}");
        if (frac > 0.20) failures.Add($"primary biome change {frac:P1} exceeds 20 %");

        // Independent worker copies agree with the root sampler.
        int disagree = 0;
        Parallel.For(0, 8, worker =>
        {
            using RingTerrainSampler mine = s7c.CreateIndependentCopy();
            if (!ReferenceEquals(climate, mine.ClimateMachines) || !ReferenceEquals(sites, mine.EmitterSites)) System.Threading.Interlocked.Increment(ref disagree);
            for (int i = worker; i < 2000; i += 8)
                if (mine.GetPresentationSurfaceHeight(pts[i].A, pts[i].X) != s7c.GetPresentationSurfaceHeight(pts[i].A, pts[i].X)) System.Threading.Interlocked.Increment(ref disagree);
        });
        Console.WriteLine($"LANDFORM_V7_WORLD_WORKERS copies=8 disagreements={disagree}");
        if (disagree != 0) failures.Add($"{disagree} worker-copy disagreements");
    }

    // ---- far-LOD aliasing -----------------------------------------------------------------------------------------

    /// <summary>
    /// Plan risk 1 measurement (not a gate): for each coarse LOD spacing, the vertical mismatch between a coarse edge (linear between samples
    /// s apart) and the exact height at the midpoint, along lines through the benchmark hill and plains tiles, for v6 and v7. Compare with the 8 m proxy skirt.
    /// </summary>
    static void LodGap(int seed)
    {
        using RingTerrainSampler s6 = EmitterSiteTestWorlds.CreateGen17Climate(seed);
        using RingTerrainSampler s7 = EmitterSiteTestWorlds.CreateGen17Landform(seed);
        var tiles = new (string Name, double Around, double Across)[] { ("hills_a", 8011003.125, 159000.125), ("hills_b", 10053024.125, 159000.125), ("hills_c", 13194594.125, 159000.125), ("plains", 2513256.125, 159000.125) };
        foreach (double spacing in new[] { 4d, 8d, 16d, 32d, 64d, 128d, 256d })
            foreach ((string name, double a0, double z0) in tiles)
            {
                var d6 = new List<double>(); var d7 = new List<double>();
                for (int line = 0; line < 12; line++)
                    foreach (bool alongAround in new[] { true, false })
                    {
                        double off = (line - 6) * 640d;
                        for (double t = -4000d; t < 4000d - spacing; t += spacing)
                        {
                            double ax = alongAround ? a0 + t : a0 + off, zx = alongAround ? z0 + off : z0 + t;
                            double bx = alongAround ? ax + spacing : ax, zb = alongAround ? zx : zx + spacing;
                            double mx = (ax + bx) * 0.5d, zm = (zx + zb) * 0.5d;
                            foreach ((RingTerrainSampler s, List<double> list) in new[] { (s6, d6), (s7, d7) })
                            {
                                double h0 = s.GetPresentationSurfaceHeight(ax, zx), h1 = s.GetPresentationSurfaceHeight(bx, zb), hm = s.GetPresentationSurfaceHeight(mx, zm);
                                list.Add(Math.Abs(hm - (h0 + h1) * 0.5d));
                            }
                        }
                    }
                d6.Sort(); d7.Sort();
                Console.WriteLine($"LANDFORM_V7_LODGAP tile={name} spacing={spacing:F0} v6_p99={Pct(d6, .99):F2} v6_max={d6[^1]:F2} v7_p99={Pct(d7, .99):F2} v7_max={d7[^1]:F2} v7_over8m={d7.Count(v => v > 8d) / (double)d7.Count:P3} n={d7.Count}");
            }
    }

    // ---- shape -------------------------------------------------------------------------------------------------

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    static List<double> Sorted(IEnumerable<double> values) { var list = values.ToList(); list.Sort(); return list; }
    static double H(TerrainGenerationHeightModel m, double a, double z) => m.SampleNaturalDiagnostics(a, z).Height;

    static double SlopeDeg(TerrainGenerationHeightModel m, double a, double z, double h0, double step)
    {
        double dx = (H(m, a + step, z) - h0) / step, dz = (H(m, a, z + step) - h0) / step;
        return Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180d / Math.PI;
    }

    static void Shape(int seed, List<string> failures)
    {
        string tag = $"seed={seed}";
        TerrainGenerationParameters p7 = EmitterSiteTestWorlds.Gen17LandformRecipe(seed), p6 = EmitterSiteTestWorlds.Gen17ClimateRecipe(seed);
        using var m7 = new TerrainGenerationHeightModel(p7, false);
        using var m6 = new TerrainGenerationHeightModel(p6, false);

        // Eligible pure-lowland sample: no mountain belt, firmly continental, inside the landform gate.
        var rng = new Random(seed ^ 0x1a4d);
        var eligible = new List<(double A, double Z, TerrainGenerationHeightModel.Diagnostics D)>();
        for (int i = 0; i < 600_000 && eligible.Count < 3000; i++)
        {
            double a = Math.Floor(rng.NextDouble() * Circumference), z = Math.Floor(rng.NextDouble() * (Width - 2));
            TerrainGenerationHeightModel.Diagnostics d = m7.SampleNaturalDiagnostics(a, z);
            if (d.AlpineWeight < .02 && d.Continentalness > .12 && d.LandformGate >= .5) eligible.Add((a, z, d));
        }
        if (eligible.Count < 3000) failures.Add($"{tag}: only {eligible.Count} eligible pure-lowland points");
        int n = Math.Max(1, eligible.Count);

        // 1. Coverage.
        double plains = eligible.Count(e => e.D.Ruggedness == 0d) / (double)n, hills = eligible.Count(e => e.D.Ruggedness >= 1d) / (double)n, rolling = 1d - plains - hills;
        double plateauAny = eligible.Count(e => e.D.PlateauWeight > 0d) / (double)n, plateauInterior = eligible.Count(e => e.D.PlateauWeight >= .8 && e.D.Ruggedness < .3) / (double)n, breaks = eligible.Count(e => e.D.PlateauWeight >= .5 && e.D.Ruggedness >= .7) / (double)n;
        Console.WriteLine($"LANDFORM_V7_SHAPE_COVERAGE {tag} eligible={eligible.Count} plains={plains:F3} rolling={rolling:F3} hills={hills:F3} plateau_any={plateauAny:F3} plateau_interior={plateauInterior:F3} breaks={breaks:F3}");
        if (plains < .29 || plains > .40) failures.Add($"{tag}: plains coverage {plains:F3} outside 0.29-0.40");
        if (hills < .24 || hills > .35) failures.Add($"{tag}: hill-country coverage {hills:F3} outside 0.24-0.35");
        if (plateauAny < .25 || plateauAny > .40) failures.Add($"{tag}: plateau coverage {plateauAny:F3} outside 0.25-0.40");

        // 2-3. Slopes at 4 m and 4 km relief windows.
        var plainSlopes = new List<double>(); var rollingSlopes = new List<double>(); var hillSlopes = new List<double>(); var breakSlopes = new List<double>();
        foreach (var e in eligible)
        {
            double slope = SlopeDeg(m7, e.A, e.Z, e.D.Height, 4d);
            if (e.D.Ruggedness == 0d) plainSlopes.Add(slope); else if (e.D.Ruggedness >= 1d) hillSlopes.Add(slope); else rollingSlopes.Add(slope);
            if (e.D.PlateauWeight >= .5 && e.D.Ruggedness >= .7) breakSlopes.Add(slope);
        }
        plainSlopes.Sort(); rollingSlopes.Sort(); hillSlopes.Sort(); breakSlopes.Sort();
        double hillOver55 = hillSlopes.Count == 0 ? 0d : hillSlopes.Count(s => s > 55d) / (double)hillSlopes.Count;
        var reliefs = new List<double>();
        foreach (var e in eligible.Where(e => e.D.Ruggedness >= 1d))
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int iz = -4; iz <= 4; iz++) for (int ia = -4; ia <= 4; ia++)
            {
                double h = H(m7, e.A + ia * 500d, Math.Clamp(e.Z + iz * 500d, 0d, Width - 2));
                lo = Math.Min(lo, h); hi = Math.Max(hi, h);
            }
            reliefs.Add(hi - lo);
        }
        reliefs.Sort();
        Console.WriteLine($"LANDFORM_V7_SHAPE_SLOPE {tag} plains_p50={Pct(plainSlopes, .5):F1} plains_p95={Pct(plainSlopes, .95):F1} rolling_p50={Pct(rollingSlopes, .5):F1} rolling_p95={Pct(rollingSlopes, .95):F1} hills_p50={Pct(hillSlopes, .5):F1} hills_p95={Pct(hillSlopes, .95):F1} hills_max={(hillSlopes.Count == 0 ? 0 : hillSlopes[^1]):F1} hills_over55={hillOver55:P2} breaks_p95={Pct(breakSlopes, .95):F1}");
        Console.WriteLine($"LANDFORM_V7_SHAPE_RELIEF {tag} hill_4km_relief_p25={Pct(reliefs, .25):F0} p50={Pct(reliefs, .5):F0} p75={Pct(reliefs, .75):F0} p95={Pct(reliefs, .95):F0} samples={reliefs.Count}");
        if (Pct(plainSlopes, .95) > 10d) failures.Add($"{tag}: plains slope p95 {Pct(plainSlopes, .95):F1} > 10 deg");
        if (Pct(hillSlopes, .95) > 35d) failures.Add($"{tag}: hill slope p95 {Pct(hillSlopes, .95):F1} > 35 deg");
        if (hillOver55 >= .005) failures.Add($"{tag}: hill-country fraction over 55 deg is {hillOver55:P2}");
        if (Pct(reliefs, .5) < 200d || Pct(reliefs, .5) > 400d) failures.Add($"{tag}: hill-country 4 km relief p50 {Pct(reliefs, .5):F0} outside 200-400 m");
        if (Pct(reliefs, .95) > 700d) failures.Add($"{tag}: hill-country 4 km relief p95 {Pct(reliefs, .95):F0} > 700 m");

        // 4. Plateau-edge transects: maximum slope along 6 km at 10 m.
        var edgeMax = new List<double>();
        foreach (var e in eligible.Where(e => e.D.PlateauWeight >= .2 && e.D.PlateauWeight <= .8).Take(300))
        {
            double prev = H(m7, e.A - 3000d, e.Z), best = 0d;
            for (int i = 1; i <= 600; i++)
            {
                double h = H(m7, e.A - 3000d + i * 10d, e.Z);
                best = Math.Max(best, Math.Abs(h - prev) / 10d);
                prev = h;
            }
            edgeMax.Add(Math.Atan(best) * 180d / Math.PI);
        }
        edgeMax.Sort();
        Console.WriteLine($"LANDFORM_V7_SHAPE_EDGES {tag} transects={edgeMax.Count} max_slope_p50={Pct(edgeMax, .5):F1} p95={Pct(edgeMax, .95):F1}");
        if (edgeMax.Count < 50 || Pct(edgeMax, .5) < 25d) failures.Add($"{tag}: plateau-edge max slope p50 {Pct(edgeMax, .5):F1} < 25 deg (transects {edgeMax.Count})");

        // 5. No inland flooding: land (continentalness > 0, macro > 0) never ends at or below sea level, with extra weight near shores.
        const int FloodSamples = 200_000;
        int flood = 0, shoreChecked = 0;
        var floodRng = new Random(seed ^ 0x77);
        var floodPoints = new (double, double)[FloodSamples];
        for (int i = 0; i < FloodSamples; i++) floodPoints[i] = (Math.Floor(floodRng.NextDouble() * Circumference), Math.Floor(floodRng.NextDouble() * (Width - 2)));
        object gate = new();
        System.Threading.Tasks.Parallel.For(0, 8, () => new TerrainGenerationHeightModel(p7, false), (w, _, model) =>
        {
            int localFlood = 0, localShore = 0;
            for (int i = w; i < FloodSamples; i += 8)
            {
                (double a, double z) = floodPoints[i];
                TerrainGenerationHeightModel.Diagnostics d = model.SampleNaturalDiagnostics(a, z);
                if (d.Continentalness > 0 && d.MacroHeight > 0)
                {
                    if (d.MacroHeight < 400d) localShore++;
                    if (d.Height <= 0d) localFlood++;
                }
            }
            lock (gate) { flood += localFlood; shoreChecked += localShore; }
            return model;
        }, model => model.Dispose());
        // Coastal bias: walk outward from known eligible points toward the shore along `across` and test every 50 m.
        foreach (var e in eligible.Take(400))
            for (int step = 0; step < 400; step++)
            {
                double z = Math.Clamp(e.Z + step * 50d, 0d, Width - 2);
                TerrainGenerationHeightModel.Diagnostics d = m7.SampleNaturalDiagnostics(e.A, z);
                if (d.Continentalness <= 0) break;
                if (d.MacroHeight > 0) { shoreChecked++; if (d.Height <= 0d) flood++; }
            }
        Console.WriteLine($"LANDFORM_V7_SHAPE_FLOOD {tag} random_points={FloodSamples} near_shore_land_points={shoreChecked} flooded_land_points={flood}");
        if (flood != 0) failures.Add($"{tag}: {flood} land points at or below sea level");

        // 6. C0: random 0.5 m pairs, and pairs straddling the gate=0 and Q=0 edges.
        int pairs = 0, pairBad = 0; double pairMax = 0d;
        var pairRng = new Random(seed ^ 0x5c0);
        for (int i = 0; i < 20_000; i++)
        {
            var e = eligible[i % n];
            double phi = pairRng.NextDouble() * Math.Tau, da = Math.Cos(phi) * .5d, dz = Math.Sin(phi) * .5d;
            double a0 = e.A + (i / n) * 7.3d + pairRng.NextDouble() * 20d, z0 = e.Z + pairRng.NextDouble() * 20d;
            double delta = Math.Abs(H(m7, a0 + da, z0 + dz) - H(m7, a0, z0));
            pairs++; pairMax = Math.Max(pairMax, delta); if (delta >= 3d) pairBad++;
        }
        int edgePairs = 0, edgeBad = 0, edgeSteep = 0; double edgeMaxDelta = 0d;
        foreach (string kind in new[] { "gate", "plateau" })
        {
            Func<TerrainGenerationHeightModel.Diagnostics, bool> inside = kind == "gate" ? d => d.LandformGate > 0d : d => d.PlateauWeight > 0d;
            int found = 0;
            for (int i = 0; i < eligible.Count && found < 200; i++)
            {
                var e = eligible[i];
                if (!inside(e.D)) continue;
                double phi = pairRng.NextDouble() * Math.Tau, ca = Math.Cos(phi), sz = Math.Sin(phi);
                double lo = 0d, hi = -1d;
                for (double r = 100d; r <= 40_000d; r += 100d)
                {
                    double z = e.Z + sz * r;
                    if (z < 0d || z > Width - 2) break;
                    if (!inside(m7.SampleNaturalDiagnostics(e.A + ca * r, z))) { hi = r; break; }
                    lo = r;
                }
                if (hi < 0d) continue;
                for (int it = 0; it < 40; it++)
                {
                    double mid = (lo + hi) * .5d;
                    if (inside(m7.SampleNaturalDiagnostics(e.A + ca * mid, e.Z + sz * mid))) lo = mid; else hi = mid;
                }
                double ha = H(m7, e.A + ca * (lo - .25d), e.Z + sz * (lo - .25d)), hb = H(m7, e.A + ca * (hi + .25d), e.Z + sz * (hi + .25d));
                double delta = Math.Abs(hb - ha);
                edgePairs++; found++; edgeMaxDelta = Math.Max(edgeMaxDelta, delta);
                if (delta >= 3d)
                {
                    // A 3 m change over 0.5 m is an 80 degree face. It is a genuine jump only if it does not shrink when the straddle narrows 50x.
                    double fine = Math.Abs(H(m7, e.A + ca * (hi + .005d), e.Z + sz * (hi + .005d)) - H(m7, e.A + ca * (lo - .005d), e.Z + sz * (lo - .005d)));
                    if (fine > .25d * delta) edgeBad++; else edgeSteep++;
                }
            }
        }
        Console.WriteLine($"LANDFORM_V7_SHAPE_C0 {tag} random_pairs={pairs} max_delta_m={pairMax:F3} bad={pairBad} edge_pairs={edgePairs} edge_max_delta_m={edgeMaxDelta:F3} edge_bad={edgeBad} edge_steep_over_3m={edgeSteep}");
        if (pairBad != 0) failures.Add($"{tag}: {pairBad} random 0.5 m pairs jump 3 m or more");
        if (edgePairs < 100 || edgeBad != 0) failures.Add($"{tag}: {edgeBad} of {edgePairs} edge-straddling pairs jump 3 m or more");

        // 7. Seam: exact periodicity and continuity across around = 0.
        int seamBad = 0; double seamStep = 0d;
        for (int i = 0; i < 256; i++)
        {
            double a = Math.Floor(pairRng.NextDouble() * Circumference), z = Math.Floor(pairRng.NextDouble() * (Width - 2));
            if (m7.SampleNaturalDiagnostics(a, z).Height != m7.SampleNaturalDiagnostics(a + Circumference, z).Height) seamBad++;
            double z2 = Math.Floor(pairRng.NextDouble() * (Width - 2));
            seamStep = Math.Max(seamStep, Math.Abs(H(m7, Circumference - .25d, z2) - H(m7, .25d, z2)));
        }
        Console.WriteLine($"LANDFORM_V7_SHAPE_SEAM {tag} periodic_mismatches={seamBad} max_step_across_seam_m={seamStep:F3}");
        if (seamBad != 0) failures.Add($"{tag}: {seamBad} points differ across a full circumference");
        if (seamStep >= 3d) failures.Add($"{tag}: {seamStep:F2} m step across the around seam");

        // 8. Determinism: a second model instance and 8 parallel worker copies agree bit-for-bit.
        var detRng = new Random(seed ^ 0xde7);
        var detPoints = Enumerable.Range(0, 2000).Select(_ => (Math.Floor(detRng.NextDouble() * Circumference), Math.Floor(detRng.NextDouble() * (Width - 2)))).ToArray();
        double[] reference = detPoints.Select(p => m7.SampleNaturalDiagnostics(p.Item1, p.Item2).Height).ToArray();
        int detBad = 0;
        using (var second = new TerrainGenerationHeightModel(p7, false))
            for (int i = 0; i < detPoints.Length; i++) if (second.SampleNaturalDiagnostics(detPoints[i].Item1, detPoints[i].Item2).Height != reference[i]) detBad++;
        System.Threading.Tasks.Parallel.For(0, 8, w =>
        {
            using var copy = new TerrainGenerationHeightModel(p7, false);
            for (int i = 0; i < detPoints.Length; i++)
                if (copy.SampleNaturalDiagnostics(detPoints[i].Item1, detPoints[i].Item2).Height != reference[i]) System.Threading.Interlocked.Increment(ref detBad);
        });
        Console.WriteLine($"LANDFORM_V7_SHAPE_DETERMINISM {tag} points={detPoints.Length} mismatches={detBad}");
        if (detBad != 0) failures.Add($"{tag}: {detBad} determinism mismatches");

        // 9. Outside the gate v7 natural terrain equals v6 exactly, and the continental fields are shared everywhere.
        const int ParitySamples = 200_000;
        var parityRng = new Random(seed ^ 0x9a);
        var parityPoints = new (double, double)[ParitySamples];
        for (int i = 0; i < ParitySamples; i++) parityPoints[i] = (Math.Floor(parityRng.NextDouble() * Circumference), Math.Floor(parityRng.NextDouble() * (Width - 2)));
        int outside = 0, outsideBad = 0, inside7 = 0, insideChanged = 0, sharedBad = 0;
        System.Threading.Tasks.Parallel.For(0, 8, () => (new TerrainGenerationHeightModel(p7, false), new TerrainGenerationHeightModel(p6, false)), (w, _, models) =>
        {
            int lo = 0, lb = 0, li = 0, lc = 0, ls = 0;
            for (int i = w; i < ParitySamples; i += 8)
            {
                (double a, double z) = parityPoints[i];
                TerrainGenerationHeightModel.Diagnostics d7 = models.Item1.SampleNaturalDiagnostics(a, z), d6 = models.Item2.SampleNaturalDiagnostics(a, z);
                if (d7.Continentalness != d6.Continentalness || d7.MacroHeight != d6.MacroHeight || d7.SeaWeight != d6.SeaWeight || d7.AlpineWeight != d6.AlpineWeight) ls++;
                if (d7.LandformGate == 0d) { lo++; if (d7.Height != d6.Height || d7.DetailHeight != d6.DetailHeight) lb++; }
                else { li++; if (d7.Height != d6.Height) lc++; }
            }
            lock (gate) { outside += lo; outsideBad += lb; inside7 += li; insideChanged += lc; sharedBad += ls; }
            return models;
        }, models => { models.Item1.Dispose(); models.Item2.Dispose(); });
        Console.WriteLine($"LANDFORM_V7_SHAPE_V6_PARITY {tag} samples={ParitySamples} outside_gate={outside} outside_gate_mismatches={outsideBad} inside_gate={inside7} inside_gate_changed={insideChanged} shared_field_mismatches={sharedBad}");
        if (outsideBad != 0) failures.Add($"{tag}: {outsideBad} outside-gate points differ from v6");
        if (sharedBad != 0) failures.Add($"{tag}: {sharedBad} points differ from v6 in continentalness/macro/sea/alpine");
        if (inside7 == 0 || insideChanged < inside7 / 2) failures.Add($"{tag}: v7 changes only {insideChanged} of {inside7} in-gate points");

        Calibration(seed);
    }

    // ---- gullies -----------------------------------------------------------------------------------------------

    static void Gullies(int seed, List<string> failures)
    {
        string tag = $"seed={seed}";
        TerrainGenerationParameters p7 = EmitterSiteTestWorlds.Gen17LandformRecipe(seed);
        TerrainGenerationParameters p0 = TerrainGenerationParameters.FromJson(WithField(p7.CanonicalJson, "lowland_erosion_strength_m", "0"));
        double strength = p7.LowlandErosionStrengthM;
        if (strength <= 0d) { Console.WriteLine($"LANDFORM_V7_GULLIES {tag} default strength is 0: gully carve checks skipped"); return; }
        using var m7 = new TerrainGenerationHeightModel(p7, false);
        using var m0 = new TerrainGenerationHeightModel(p0, false);
        var rng = new Random(seed ^ 0x6011);
        var eligible = new List<(double A, double Z, TerrainGenerationHeightModel.Diagnostics D)>();
        for (int i = 0; i < 600_000 && eligible.Count < 4000; i++)
        {
            double a = Math.Floor(rng.NextDouble() * Circumference), z = Math.Floor(rng.NextDouble() * (Width - 2));
            TerrainGenerationHeightModel.Diagnostics d = m7.SampleNaturalDiagnostics(a, z);
            if (d.AlpineWeight < .02 && d.Continentalness > .12 && d.LandformGate >= .5) eligible.Add((a, z, d));
        }
        if (eligible.Count < 4000) failures.Add($"{tag}: only {eligible.Count} eligible points");

        // Carve range and the plains scaling: |carve| <= min(strength, landform/2), and <= 0.3 x strength where ruggedness is 0.
        int carved = 0, plainsPts = 0, plainsCarved = 0, hillsPts = 0, hillsCarved = 0, rangeBad = 0, differenceBad = 0;
        double maxCarve = 0d, maxPlainsCarve = 0d;
        foreach (var e in eligible)
        {
            TerrainGenerationHeightModel.Diagnostics d = e.D, z = m0.SampleNaturalDiagnostics(e.A, e.Z);
            double carve = d.LowlandErosionHeight;
            if (carve > 0d || carve < -Math.Min(strength, .5d * d.LandformHeight) - 1e-9) rangeBad++;
            if (z.LowlandErosionHeight != 0d) differenceBad++;
            if (!d.Clamped && Math.Abs(d.Height - (z.Height + carve)) > 1e-9) differenceBad++;
            if (carve < 0d) carved++;
            if (d.Ruggedness == 0d) { plainsPts++; if (carve < 0d) plainsCarved++; maxPlainsCarve = Math.Max(maxPlainsCarve, -carve); }
            else if (d.Ruggedness >= 1d) { hillsPts++; if (carve < 0d) hillsCarved++; }
            maxCarve = Math.Max(maxCarve, -carve);
        }
        double plainsFraction = plainsPts == 0 ? 0d : plainsCarved / (double)plainsPts, hillsFraction = hillsPts == 0 ? 0d : hillsCarved / (double)hillsPts;
        Console.WriteLine($"LANDFORM_V7_GULLIES_CARVE {tag} eligible={eligible.Count} carved={carved} plains_carved_fraction={plainsFraction:P1} hills_carved_fraction={hillsFraction:P1} max_carve_m={maxCarve:F2} max_plains_carve_m={maxPlainsCarve:F2}");
        if (rangeBad != 0) failures.Add($"{tag}: {rangeBad} carve values outside [-min(strength, landform/2), 0]");
        if (differenceBad != 0) failures.Add($"{tag}: {differenceBad} points where default minus carve differs from the gully-free model");
        if (carved == 0) failures.Add($"{tag}: no gully carve found at all");
        if (hillsPts > 0 && hillsFraction <= plainsFraction) failures.Add($"{tag}: hill carve fraction {hillsFraction:P1} is not above the plains fraction {plainsFraction:P1}");
        if (maxPlainsCarve > strength * .3d + 1e-9) failures.Add($"{tag}: plains carve {maxPlainsCarve:F2} m exceeds 0.3x strength");

        // C0 across carved ground: 0.5 m pairs.
        int pairs = 0, bad = 0; double pairMax = 0d;
        var pairRng = new Random(seed ^ 0x5c1);
        foreach (var e in eligible.Where(e => e.D.LowlandErosionHeight < 0d).Take(10_000))
        {
            double phi = pairRng.NextDouble() * Math.Tau, da = Math.Cos(phi) * .5d, dz = Math.Sin(phi) * .5d;
            double delta = Math.Abs(H(m7, e.A + da, e.Z + dz) - e.D.Height);
            pairs++; pairMax = Math.Max(pairMax, delta); if (delta >= 3d) bad++;
        }
        Console.WriteLine($"LANDFORM_V7_GULLIES_C0 {tag} carved_pairs={pairs} max_delta_m={pairMax:F3} bad={bad}");
        if (bad != 0) failures.Add($"{tag}: {bad} carved-ground 0.5 m pairs jump 3 m or more");

        // Reverse order on parallel workers must agree bit for bit (the stencil cache must not leak between samples).
        var points = eligible.Take(2000).Select(e => (e.A, e.Z)).ToArray();
        double[] reference = points.Select(q => m7.SampleNaturalDiagnostics(q.A, q.Z).Height).ToArray();
        int mismatches = 0;
        System.Threading.Tasks.Parallel.For(0, 8, w =>
        {
            using var copy = new TerrainGenerationHeightModel(p7, false);
            for (int i = points.Length - 1; i >= 0; i--)
                if (copy.SampleNaturalDiagnostics(points[i].A, points[i].Z).Height != reference[i]) System.Threading.Interlocked.Increment(ref mismatches);
        });
        Console.WriteLine($"LANDFORM_V7_GULLIES_DETERMINISM {tag} points={points.Length} mismatches={mismatches}");
        if (mismatches != 0) failures.Add($"{tag}: {mismatches} order/worker determinism mismatches with gullies enabled");
    }

    /// <summary>Scan helper that prints 256 m tile origins whose corners and centre all satisfy a class, for the hard-coded benchmark locations.</summary>
    static void LocateBenchmarkTiles(int seed)
    {
        using var model = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17LandformRecipe(seed), false);
        var predicates = new (string Name, Func<TerrainGenerationHeightModel.Diagnostics, bool> Test)[]
        {
            ("plains", d => d.LandformGate >= .999 && d.Ruggedness == 0d && d.PlateauWeight == 0d && d.AlpineWeight < .001),
            ("hills", d => d.LandformGate >= .999 && d.Ruggedness >= 1d && d.PlateauWeight == 0d && d.AlpineWeight < .001),
            ("edge", d => d.LandformGate >= .999 && d.PlateauWeight is > .3 and < .7 && d.Ruggedness >= .5 && d.AlpineWeight < .001),
        };
        foreach (var (name, test) in predicates)
        {
            int found = 0;
            for (int zi = 0; zi < 60 && found < 3; zi++) for (int ai = 0; ai < 200 && found < 3; ai++)
            {
                double a = Math.Floor(ai * 78_539.25) + .125, z = 159_000.125 + zi * 7_950d;
                bool all = true;
                foreach ((double da, double dz) in new[] { (0d, 0d), (254d, 0d), (0d, 254d), (254d, 254d), (127d, 127d) })
                    if (!test(model.SampleNaturalDiagnostics(a + da, z + dz))) { all = false; break; }
                if (all) { found++; Console.WriteLine($"LANDFORM_V7_TILE {name} around={a:R} across={z:R}"); }
            }
        }
    }

    /// <summary>Prints the raw R/Q unit-value percentiles and |hn| before its clamp, using the same noise recipes on an unwarped field (warping only displaces coordinates).</summary>
    static void Calibration(int seed)
    {
        TerrainGenerationParameters p = EmitterSiteTestWorlds.Gen17LandformRecipe(seed);
        double radius = p.CircumferenceM / Math.Tau;
        FastNoiseLite Make(int offset, double scale, int octaves) => new() { Seed = unchecked((int)(p.Seed + offset)), NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = (float)(1d / scale), FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = octaves, FractalGain = (float)p.NoiseGain, FractalLacunarity = (float)p.NoiseLacunarity };
        using FastNoiseLite landform = Make(1201, p.LandformScaleM, 3), plateau = Make(1301, p.PlateauScaleM, 2), hillBase = Make(1409, p.HillScaleM, 2), hillDetail = Make(1423, p.HillScaleM / 4d, 2);
        var rng = new Random(seed ^ 0xca1);
        var r = new List<double>(); var q = new List<double>(); var hn = new List<double>();
        for (int i = 0; i < 40_000; i++)
        {
            double a = rng.NextDouble() * p.CircumferenceM, z = rng.NextDouble() * p.WidthM, theta = a / p.CircumferenceM * Math.Tau;
            float x = (float)(Math.Cos(theta) * radius), y = (float)(Math.Sin(theta) * radius);
            r.Add(Math.Clamp(landform.GetNoise3D(x, y, (float)z) * .5 + .5, 0, 1)); q.Add(Math.Clamp(plateau.GetNoise3D(x, y, (float)z) * .5 + .5, 0, 1));
            hn.Add(Math.Abs((hillBase.GetNoise3D(x, y, (float)z) + p.HillDetailWeight * hillDetail.GetNoise3D(x, y, (float)z)) / ((1d + p.HillDetailWeight) * HillNormalizationForCalibration)));
        }
        r.Sort(); q.Sort(); hn.Sort();
        Console.WriteLine($"LANDFORM_V7_CALIBRATION seed={seed} R_unit p5/p25/p50/p75/p95={Pct(r, .05):F3}/{Pct(r, .25):F3}/{Pct(r, .5):F3}/{Pct(r, .75):F3}/{Pct(r, .95):F3} Q_unit p5/p25/p50/p75/p95={Pct(q, .05):F3}/{Pct(q, .25):F3}/{Pct(q, .5):F3}/{Pct(q, .75):F3}/{Pct(q, .95):F3} abs_hn p50/p90/p99/max={Pct(hn, .5):F3}/{Pct(hn, .9):F3}/{Pct(hn, .99):F3}/{hn[^1]:F3} clamped_fraction={hn.Count(v => v > 1d) / (double)hn.Count:P2}");
    }

    const double HillNormalizationForCalibration = 0.48d;
}
