using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-G1 of docs/gpu-offload-plan.md (section 4.5). Compares the GPU K1 kernel (materials/terrain_gpu/ring_proxy_height_k1.glsl: v7/v8 natural
/// height plus regional climate and biome weights) against the C# runtime sampler, which is what TerrainBuildSampleCache.Get reads with edits off:
/// RingTerrainSampler.GetPresentationSurfaceHeight (height), GetTerrainSample (continentalness, erosion, temperature, moisture, ridge) and
/// GetBiomeTextureWeights. Fixtures: about 40 sectors per spacing 2, 4, 8, 16 and 32 (deep basin, trench, shelf, coast, plains, hill country,
/// plateau edges and tops, mountain gullies, foothills, ring seam with negative sector indices, across edges, far around), each as the 67 x 67
/// height window plus the 16 m biome-centre lattice, and 10^6 uniformly random ring points for height and climate. Points inside an emitter-site
/// or Weather Lung influence (the sampler differs from the natural model) are CPU-routed in the plan and are excluded and counted.
/// The v6 recipe (and v1-v5) must be reported unsupported.
/// Tolerances (4.5): height p99.9 abs diff <= 0.02 m, max <= 0.25 m, mean <= 1e-3 m; water kind flips only where abs(h) < 0.05 m;
/// biome weights max channel diff <= 2/255 and categorical flips <= 0.1 %.
/// Run (windowed Vulkan, not --headless): Godot --path . --script res://tools/proxy_gpu_height_parity_test.gd
/// Prints PROXY_GPU_HEIGHT_PARITY=PASS|FAIL and writes .godot/proxy_gpu_height_parity/report.json and report.txt.
/// </summary>
public partial class ProxyGpuHeightParityTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const int Width = 318_000;
    private const int Cells = 64;
    private const int Window = 67;
    private const int RandomPoints = 1_000_000;
    private const double HeightP999 = 0.02, HeightMax = 0.25, HeightMean = 1e-3, WaterFlipHeight = 0.05;
    private const double WeightMax255 = 2.0, FlipFraction = 0.001;
    private static readonly int[] Spacings = { 2, 4, 8, 16, 32 };

    private RenderingDevice _rd = null!;
    private readonly List<string> _lines = new();
    private readonly List<string> _failures = new();
    private readonly Dictionary<string, object> _json = new();

    private sealed class Worker
    {
        public RingTerrainSampler Sampler = null!;
        public TerrainGenerationHeightModel Model = null!;
    }

    private sealed class Recipe : IDisposable
    {
        public string Name = "";
        public TerrainGenerationParameters Parameters = null!;
        public ProxyGpuClimateSettings Climate = null!;
        public ConcurrentBag<Worker> Pool = new();
        public void Dispose()
        {
            foreach (Worker w in Pool) { w.Sampler.Dispose(); w.Model.Dispose(); }
            Pool.Clear();
        }
    }

    private void Log(string line) { _lines.Add(line); Console.WriteLine("PROXY_GPU_HEIGHT_DETAIL " + line); }

    public int Run(string outputDirectory)
    {
        try
        {
            _rd = RenderingServer.CreateLocalRenderingDevice();
            if (_rd == null) { Console.Error.WriteLine("PROXY_GPU_HEIGHT_PARITY=FAIL no local RenderingDevice"); return 1; }
            Log($"device: {_rd.GetDeviceName()} ({_rd.GetDeviceVendorName()})");
            _json["device"] = _rd.GetDeviceName();

            CheckUnsupportedRecipes();

            string showcaseJson = ReadShowcaseRecipe();
            var recipes = new List<Recipe>
            {
                CreateRecipe("v8_showcase", TerrainGenerationParameters.FromJson(showcaseJson)),
                CreateRecipe("v7_default", TerrainGenerationParameters.LandformDefault()),
            };
            var timingFixtures = new List<(string Label, long Around, int Across)>();
            foreach (Recipe recipe in recipes)
            {
                try { RunRecipe(recipe, timingFixtures); }
                finally { recipe.Dispose(); }
            }
            _json["done"] = true;
            string dir = string.IsNullOrEmpty(outputDirectory) ? ProjectSettings.GlobalizePath("res://.godot/proxy_gpu_height_parity") : outputDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(_json, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(dir, "report.txt"), string.Join("\n", _lines) + "\n");
        }
        catch (Exception e) { Console.Error.WriteLine("PROXY_GPU_HEIGHT_PARITY=FAIL exception " + e); return 1; }
        finally { _rd?.Free(); }

        if (_failures.Count != 0) { Console.Error.WriteLine("PROXY_GPU_HEIGHT_PARITY=FAIL " + string.Join(" | ", _failures.Take(24))); return 1; }
        Console.WriteLine("PROXY_GPU_HEIGHT_PARITY=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- recipes and workers

    private static string ReadShowcaseRecipe()
    {
        string text = Godot.FileAccess.GetFileAsString("res://resources/menu/showcase_world.json");
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("generation_settings").GetProperty("surface_generation_recipe_json").GetString()!;
    }

    private static RingTerrainSampler CreateSampler(TerrainGenerationParameters recipe, out VoxelTerrainSettings settings)
    {
        settings = new VoxelTerrainSettings
        {
            Seed = unchecked((int)recipe.Seed),
            SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
            BedrockY = -8000, SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false, EnableProceduralCaves = false,
            EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false,
        };
        return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
    }

    private static int ChannelOf(object id) => id.ToString() switch { "Desert" => 0, "Forest" => 1, "Tundra" => 2, "Alpine" => 3, _ => -1 };

    private Recipe CreateRecipe(string name, TerrainGenerationParameters parameters)
    {
        var recipe = new Recipe { Name = name, Parameters = parameters };
        int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 12);
        for (int i = 0; i < workers; i++)
        {
            RingTerrainSampler sampler = CreateSampler(parameters, out VoxelTerrainSettings settings);
            if (i == 0)
            {
                var c = new ProxyGpuClimateSettings
                {
                    Seed = settings.Seed, AroundRingLength = Circumference, RingRadius = Circumference / Math.Tau, RingWidth = Width,
                    ContinentalFrequency = settings.ContinentalFrequency, ErosionFrequency = settings.ErosionFrequency, RidgeFrequency = settings.RidgeFrequency,
                    TemperatureFrequency = settings.TemperatureFrequency, MoistureFrequency = settings.MoistureFrequency,
                    MountainWarpFrequency = settings.MountainWarpFrequency, ValleyFrequency = settings.ValleyFrequency,
                    ContinentalSecondaryStrength = settings.ContinentalSecondaryStrength, ContinentalContrast = settings.ContinentalContrast,
                    ContinentalOceanBias = settings.ContinentalOceanBias, MountainWarpDistanceMeters = settings.MountainWarpDistanceMeters,
                    PeakRarityExponent = settings.PeakRarityExponent, MountainBeltBroadness = settings.MountainBeltBroadness,
                    ValleyNarrowness = settings.ValleyNarrowness, OceanMoistureInfluence = settings.OceanMoistureInfluence,
                    ClimateLatitudeInfluence = settings.ClimateLatitudeInfluence, CoastalBeachStrength = settings.CoastalBeachStrength,
                    AltitudeTemperatureDropPerKilometre = settings.AltitudeTemperatureDropPerKilometre,
                };
                // The sampler's captured biome profiles (catalog or built-in defaults), read as the CPU reference uses them.
                FieldInfo field = typeof(RingTerrainSampler).GetField("_biomeProfiles", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (object profile in (Array)field.GetValue(sampler)!)
                {
                    Type t = profile.GetType();
                    object id = t.GetProperty("Id")!.GetValue(profile)!;
                    string idName = id.ToString()!;
                    if (idName is "Ocean" or "Lake" or "River") continue;
                    float F(string n) => (float)t.GetProperty(n)!.GetValue(profile)!;
                    c.Profiles.Add(new ProxyGpuClimateSettings.Profile(ChannelOf(id), (int)t.GetProperty("Priority")!.GetValue(profile)!,
                        F("MinimumTemperature"), F("MaximumTemperature"), F("MinimumMoisture"), F("MaximumMoisture"),
                        F("MinimumRidge"), F("MaximumRidge"), F("BlendWidth"), F("SelectionWeightMultiplier")));
                }
                recipe.Climate = c;
            }
            settings.Dispose();
            recipe.Pool.Add(new Worker { Sampler = sampler, Model = new TerrainGenerationHeightModel(parameters) });
        }
        return recipe;
    }

    private static void WithWorker(Recipe recipe, Action<Worker> action)
    {
        Worker? w;
        while (!recipe.Pool.TryTake(out w)) System.Threading.Thread.Sleep(1);
        try { action(w); } finally { recipe.Pool.Add(w); }
    }

    // ---------------------------------------------------------------- unsupported recipes

    private void CheckUnsupportedRecipes()
    {
        (string Name, TerrainGenerationParameters P, bool Expected)[] cases =
        {
            ("v1_legacy", TerrainGenerationParameters.LegacyDefault(), false),
            ("v2_coastal", TerrainGenerationParameters.CoastalDefault(), false),
            ("v3_detail", TerrainGenerationParameters.Default(), false),
            ("v4_erosion", TerrainGenerationParameters.ErosionDefault(), false),
            ("v5_sites", TerrainGenerationParameters.SitesDefault(), false),
            ("v6_climate", TerrainGenerationParameters.ClimateDefault(), false),
            ("v7_landform", TerrainGenerationParameters.LandformDefault(), true),
            ("v8_weather_lung", TerrainGenerationParameters.WeatherLungDefault(), true),
        };
        foreach (var (name, p, expected) in cases)
        {
            bool supported = ProxyGpuHeightKernel.IsSupported(p, out string reason);
            Log($"support {name,-16} gpu_supported={supported}{(supported ? "" : " (" + reason + ")")}");
            if (supported != expected) _failures.Add($"support {name}: expected {expected}, got {supported}");
        }
        try
        {
            var dummy = new ProxyGpuClimateSettings();
            using var kernel = new ProxyGpuHeightKernel(_rd, TerrainGenerationParameters.ClimateDefault(), dummy);
            _failures.Add("v6 recipe was accepted by the kernel constructor");
        }
        catch (NotSupportedException e) { Log("v6 kernel construction refused as expected: " + e.Message); }
    }

    // ---------------------------------------------------------------- fixtures

    private readonly record struct Position(string Label, long Around, int Across);

    private static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q; }

    private List<Position> DiscoverPositions(Recipe recipe)
    {
        // Natural-model scan: every 4096 m around x 40 across lines, classified by the model's own diagnostics.
        const int StepA = 4096;
        int columns = (int)(Circumference / StepA);
        var found = new ConcurrentBag<(string Class, long Around, int Across)>();
        Parallel.For(0, columns, col =>
        {
            WithWorker(recipe, w =>
            {
                long around = (long)col * StepA + 1024;
                for (int line = 0; line < 40; line++)
                {
                    int across = 4000 + line * 7900;
                    var d = w.Model.SampleNaturalDiagnostics(around, across);
                    string? cls = null;
                    if (d.TrenchDepth > 500d) cls = "trench";
                    else if (d.Height < -3800d) cls = "deep_basin";
                    else if (d.Height < -30d && d.Height > -250d) cls = "shelf";
                    else if (Math.Abs(d.Height) < 25d) cls = "coast";
                    else if (d.ErosionHeight < -8d) cls = "mountain_gullies";
                    else if (d.FoothillHeight > 30d && d.AlpineWeight < 0.3) cls = "foothill";
                    else if (d.LandformGate > 0.9 && d.PlateauWeight > 0.05 && d.PlateauWeight < 0.95) cls = "plateau_edge";
                    else if (d.LandformGate > 0.9 && d.PlateauWeight >= 0.95) cls = "plateau_top";
                    else if (d.LandformGate > 0.9 && d.Ruggedness < 0.05) cls = "plains";
                    else if (d.LandformGate > 0.9 && d.Ruggedness > 0.95) cls = "hill_country";
                    else if (d.AlpineWeight > 0.9 && d.Height > 2000d) cls = "mountain_belt";
                    if (cls != null) found.Add((cls, around, across));
                }
            });
        });
        var positions = new List<Position>();
        foreach (var group in found.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.OrderBy(f => f.Around).ThenBy(f => f.Across).ToList();
            int pick = Math.Min(4, list.Count);
            Log($"class {group.Key,-16} candidates={list.Count} picked={pick}");
            for (int i = 0; i < pick; i++)
            {
                var f = list[(int)((i + 0.5) * list.Count / pick)];
                positions.Add(new Position($"{group.Key}#{i}", f.Around, f.Across));
            }
        }
        return positions;
    }

    private static List<Position> SeamAndEdgePositions()
    {
        var p = new List<Position>
        {
            new("seam_zero_a", 0, 100_000), new("seam_zero_b", 0, 240_000),
            new("seam_last_a", Circumference - 1, 100_000), new("seam_last_b", Circumference - 1, 240_000),
            new("seam_negative_1", -1, 160_000), new("seam_negative_5", -5 * 4096L, 60_000),
            new("far_around_a", 31_000_000, 100_000), new("far_around_b", 31_300_000, 220_000),
            new("across_zero_a", 15_700_000, 0), new("across_zero_b", 9_000_000, 1),
            new("across_edge_a", 15_700_000, Width - 1), new("across_edge_b", 25_000_000, Width - 1),
        };
        return p;
    }

    private readonly record struct SectorJob(string Label, int Spacing, long Origin, int OriginAcross);

    private static SectorJob MakeSector(Position p, int spacing)
    {
        long size = (long)Cells * spacing;
        return new SectorJob(p.Label, spacing, FloorDiv(p.Around, size) * size, (int)(FloorDiv(p.Across, size) * size));
    }

    // ---------------------------------------------------------------- statistics

    private sealed class Acc
    {
        public string Name;
        private float[] _diffs = new float[1024];
        public long Count, Exact;
        public double Sum, Max;
        public string Worst = "";
        public Acc(string name) { Name = name; }
        public void Add(double cpu, double gpu, Func<string> where)
        {
            double d = Math.Abs(cpu - gpu);
            if (double.IsNaN(gpu) || double.IsNaN(cpu)) d = double.PositiveInfinity;
            if (Count == _diffs.Length) Array.Resize(ref _diffs, _diffs.Length * 2);
            _diffs[Count++] = (float)d;
            if (d == 0d) Exact++;
            Sum += d;
            if (d > Max) { Max = d; Worst = where(); }
        }
        public double Percentile(double fraction)
        {
            if (Count == 0) return 0;
            var copy = new float[Count];
            Array.Copy(_diffs, copy, Count);
            Array.Sort(copy);
            return copy[Math.Min(Count - 1, (long)Math.Ceiling(fraction * Count) - 1)];
        }
        public double Mean => Count == 0 ? 0 : Sum / Count;
        public double ExactPercent => Count == 0 ? 100 : 100.0 * Exact / Count;
    }

    private sealed class Totals
    {
        public readonly Dictionary<string, Acc> Accs = new();
        public long Sites, WaterFlips, WaterFlipsBad, CategoricalFlips, CategoricalCompared, WaterPoints, Points;
        public double WorstFlipHeight;
        public string WorstFlip = "";
        public Acc Get(string name) { if (!Accs.TryGetValue(name, out Acc? a)) Accs[name] = a = new Acc(name); return a; }
    }

    private static string AccRow(Acc a, string unit, double scale = 1d)
        => $"n={a.Count} p99.9={a.Percentile(0.999) * scale:E3}{unit} max={a.Max * scale:E3}{unit} mean={a.Mean * scale:E3}{unit} exact={a.ExactPercent:F3}% worst@{a.Worst}";

    // ---------------------------------------------------------------- reference values

    private sealed class RefPoints
    {
        public int N;
        public float[] Height = Array.Empty<float>(), Natural = Array.Empty<float>();
        public float[] Cont = Array.Empty<float>(), Erosion = Array.Empty<float>(), Temp = Array.Empty<float>(), Moist = Array.Empty<float>(), Ridge = Array.Empty<float>();
        public float[] WD = Array.Empty<float>(), WF = Array.Empty<float>(), WT = Array.Empty<float>(), WA = Array.Empty<float>();
        public bool[] Site = Array.Empty<bool>(), Valid = Array.Empty<bool>();
        public long[] Around = Array.Empty<long>();
        public int[] Across = Array.Empty<int>();
    }

    private static void FillReference(Worker w, RefPoints r, int i, long around, int across, bool climate)
    {
        long wrapped = ((around % Circumference) + Circumference) % Circumference;
        int clampedAcross = Math.Clamp(across, 0, Width);
        r.Around[i] = around; r.Across[i] = across;
        r.Height[i] = w.Sampler.GetPresentationSurfaceHeight(around, clampedAcross);
        r.Natural[i] = (float)w.Model.SampleNaturalDiagnostics(wrapped, Math.Clamp(across, 0, Width - 2)).Height;
        bool site = Math.Abs(r.Natural[i] - r.Height[i]) > 0.01f;
        if (climate)
        {
            TerrainSample s = w.Sampler.GetTerrainSample(around, clampedAcross);
            RingTerrainSampler.BiomeTextureWeights bw = w.Sampler.GetBiomeTextureWeights(s);
            r.Cont[i] = s.Continentalness; r.Erosion[i] = s.Erosion; r.Temp[i] = s.Temperature; r.Moist[i] = s.Moisture; r.Ridge[i] = s.RidgeStrength;
            r.WD[i] = bw.Desert; r.WF[i] = bw.Forest; r.WT[i] = bw.Tundra; r.WA[i] = bw.Alpine;
            site |= s.SiteMask.Hardscape > 0f || s.SiteMask.Rock > 0f || s.SiteMask.Sand > 0f;
        }
        r.Site[i] = site;
        r.Valid[i] = true;
    }

    private static RefPoints NewRef(int n)
    {
        return new RefPoints
        {
            N = n, Height = new float[n], Natural = new float[n], Cont = new float[n], Erosion = new float[n], Temp = new float[n], Moist = new float[n],
            Ridge = new float[n], WD = new float[n], WF = new float[n], WT = new float[n], WA = new float[n], Site = new bool[n], Valid = new bool[n],
            Around = new long[n], Across = new int[n],
        };
    }

    private static float G(float[] g, int i, int field) => g[i * ProxyGpuHeightKernel.OutputFloats + field];

    private void CompareHeight(Totals t, string group, RefPoints r, float[] gpu, string label)
    {
        for (int i = 0; i < r.N; i++)
        {
            if (!r.Valid[i]) continue;
            t.Points++;
            if (r.Site[i]) { t.Sites++; continue; }
            float g = G(gpu, i, 0), c = r.Height[i];
            int ii = i;
            t.Get(group + ":height").Add(c, g, () => $"{label} around={r.Around[ii]} across={r.Across[ii]} cpu={c:R} gpu={g:R}");
            bool wc = c < 0f, wg = g < 0f;
            if (wc) t.WaterPoints++;
            if (wc != wg)
            {
                t.WaterFlips++;
                if (Math.Abs(c) >= WaterFlipHeight) { t.WaterFlipsBad++; }
                if (Math.Abs(c) > t.WorstFlipHeight || t.WorstFlip.Length == 0) { t.WorstFlipHeight = Math.Abs(c); t.WorstFlip = $"{label} around={r.Around[ii]} across={r.Across[ii]} cpu={c:R} gpu={g:R}"; }
            }
        }
    }

    private void CompareClimate(Totals t, string group, RefPoints r, float[] gpu, string label)
    {
        for (int i = 0; i < r.N; i++)
        {
            if (!r.Valid[i] || r.Site[i]) continue;
            int ii = i;
            float gh = G(gpu, i, 0), ch = r.Height[i];
            if ((ch < 0f) != (gh < 0f)) continue; // water-flip cells: weights jump to or from the water colour, counted in the height pass
            string W() => $"{label} around={r.Around[ii]} across={r.Across[ii]}";
            t.Get(group + ":continentalness").Add(r.Cont[i], G(gpu, i, 1), W);
            t.Get(group + ":ridge").Add(r.Ridge[i], G(gpu, i, 2), W);
            t.Get(group + ":erosion").Add(r.Erosion[i], G(gpu, i, 4), W);
            t.Get(group + ":temperature").Add(r.Temp[i], G(gpu, i, 5), W);
            t.Get(group + ":moisture").Add(r.Moist[i], G(gpu, i, 6), W);
            float[] cw = { r.WD[i], r.WF[i], r.WT[i], r.WA[i] };
            float[] gw = { G(gpu, i, 8), G(gpu, i, 9), G(gpu, i, 10), G(gpu, i, 11) };
            double worstChannel = 0d;
            int cArg = 0, gArg = 0;
            for (int k = 0; k < 4; k++)
            {
                worstChannel = Math.Max(worstChannel, Math.Abs(cw[k] - gw[k]) * 255d);
                if (cw[k] > cw[cArg]) cArg = k;
                if (gw[k] > gw[gArg]) gArg = k;
            }
            t.Get(group + ":weights_255").Add(0d, worstChannel, W);
            t.CategoricalCompared++;
            if (cArg != gArg) t.CategoricalFlips++;
        }
    }

    // ---------------------------------------------------------------- one recipe

    private void RunRecipe(Recipe recipe, List<(string Label, long Around, int Across)> timingFixtures)
    {
        Log($"=== recipe {recipe.Name} ({recipe.Parameters.GeneratorId}) ===");
        using var kernel = new ProxyGpuHeightKernel(_rd, recipe.Parameters, recipe.Climate);
        Log($"kernel compiled; recipe words={kernel.RecipeWords}, climate profiles={recipe.Climate.Profiles.Count}");

        List<Position> positions = DiscoverPositions(recipe);
        positions.AddRange(SeamAndEdgePositions());
        var jobs = new List<SectorJob>();
        foreach (int spacing in Spacings) foreach (Position p in positions) jobs.Add(MakeSector(p, spacing));
        Log($"fixtures: {positions.Count} locations x {Spacings.Length} spacings = {jobs.Count} sectors");

        // CPU reference for every sector: 67 x 67 height window plus the 16 m biome-centre lattice.
        var heightRefs = new RefPoints[jobs.Count];
        var climateRefs = new RefPoints[jobs.Count];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, jobs.Count, ji =>
        {
            SectorJob j = jobs[ji];
            WithWorker(recipe, w =>
            {
                var hr = NewRef(Window * Window);
                for (int iy = 0; iy < Window; iy++)
                    for (int ix = 0; ix < Window; ix++)
                        FillReference(w, hr, iy * Window + ix, j.Origin + (long)(ix - 1) * j.Spacing, j.OriginAcross + (iy - 1) * j.Spacing, false);
                heightRefs[ji] = hr;
                int cellsPerSide = Cells * j.Spacing / 16;
                var cr = NewRef(cellsPerSide * cellsPerSide);
                for (int iy = 0; iy < cellsPerSide; iy++)
                    for (int ix = 0; ix < cellsPerSide; ix++)
                    {
                        int across = j.OriginAcross + 8 + iy * 16;
                        if (across > Width - 2) continue; // clamped / odd presentation rows are outside the lattice contract
                        FillReference(w, cr, iy * cellsPerSide + ix, j.Origin + 8 + (long)ix * 16, across, true);
                    }
                climateRefs[ji] = cr;
            });
        });
        Log($"CPU reference for sectors: {sw.Elapsed.TotalSeconds:F1} s");

        var totals = new Totals();
        for (int ji = 0; ji < jobs.Count; ji++)
        {
            SectorJob j = jobs[ji];
            long anchor = ((j.Origin % Circumference) + Circumference) % Circumference;
            string label = $"{j.Label}/S{j.Spacing}";
            kernel.RunLattice(anchor, Circumference, -j.Spacing, j.OriginAcross - j.Spacing, j.Spacing, Window, Window, 1, 0, false, out float[] hg);
            CompareHeight(totals, $"S{j.Spacing}", heightRefs[ji], hg, label);
            int cellsPerSide = Cells * j.Spacing / 16;
            kernel.RunLattice(anchor, Circumference, 8, j.OriginAcross + 8, 16, cellsPerSide, cellsPerSide, 1, 0, true, out float[] cg);
            CompareClimate(totals, $"S{j.Spacing}", climateRefs[ji], cg, label);
            // Climate height is also compared at the centres (same kernel, separate points).
            CompareHeight(totals, $"S{j.Spacing}c", climateRefs[ji], cg, label);
        }

        RunRandomPoints(recipe, kernel, totals);
        Report(recipe, totals);
        if (recipe.Name == "v8_showcase") RunTimings(kernel, positions);
    }

    private void RunRandomPoints(Recipe recipe, ProxyGpuHeightKernel kernel, Totals totals)
    {
        var rng = new Random(20261105);
        var around = new long[RandomPoints];
        var across = new int[RandomPoints];
        for (int i = 0; i < RandomPoints; i++)
        {
            around[i] = (long)(rng.NextDouble() * (Circumference / 2)) * 2;
            across[i] = rng.Next(0, (Width - 2) / 2 + 1) * 2;
        }
        var r = NewRef(RandomPoints);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int chunk = 4096;
        Parallel.For(0, (RandomPoints + chunk - 1) / chunk, ci =>
        {
            WithWorker(recipe, w =>
            {
                int end = Math.Min(RandomPoints, (ci + 1) * chunk);
                for (int i = ci * chunk; i < end; i++) FillReference(w, r, i, around[i], across[i], true);
            });
        });
        Log($"CPU reference for {RandomPoints} random points: {sw.Elapsed.TotalSeconds:F1} s");

        const int batch = 125_000;
        var points = new uint[batch * 8];
        for (int start = 0; start < RandomPoints; start += batch)
        {
            int n = Math.Min(batch, RandomPoints - start);
            for (int i = 0; i < n; i++)
            {
                long a = around[start + i];
                long anchor = (a / 4096) * 4096;
                var trig = ProxyGpuHeightKernel.AnchorTrig(anchor, Circumference);
                int b = i * 8;
                points[b] = (uint)anchor; points[b + 1] = (uint)(a - anchor); points[b + 2] = (uint)across[start + i]; points[b + 3] = 1u;
                points[b + 4] = BitConverter.SingleToUInt32Bits(trig.CosHi); points[b + 5] = BitConverter.SingleToUInt32Bits(trig.CosLo);
                points[b + 6] = BitConverter.SingleToUInt32Bits(trig.SinHi); points[b + 7] = BitConverter.SingleToUInt32Bits(trig.SinLo);
            }
            kernel.RunPoints(points, n, out float[] g);
            var slice = SliceRef(r, start, n);
            CompareHeight(totals, "random", slice, g, "random");
            CompareClimate(totals, "random", slice, g, "random");
        }
    }

    private static RefPoints SliceRef(RefPoints r, int start, int n)
    {
        RefPoints s = NewRef(n);
        Array.Copy(r.Height, start, s.Height, 0, n); Array.Copy(r.Natural, start, s.Natural, 0, n);
        Array.Copy(r.Cont, start, s.Cont, 0, n); Array.Copy(r.Erosion, start, s.Erosion, 0, n); Array.Copy(r.Temp, start, s.Temp, 0, n);
        Array.Copy(r.Moist, start, s.Moist, 0, n); Array.Copy(r.Ridge, start, s.Ridge, 0, n);
        Array.Copy(r.WD, start, s.WD, 0, n); Array.Copy(r.WF, start, s.WF, 0, n); Array.Copy(r.WT, start, s.WT, 0, n); Array.Copy(r.WA, start, s.WA, 0, n);
        Array.Copy(r.Site, start, s.Site, 0, n); Array.Copy(r.Valid, start, s.Valid, 0, n); Array.Copy(r.Around, start, s.Around, 0, n); Array.Copy(r.Across, start, s.Across, 0, n);
        return s;
    }

    // ---------------------------------------------------------------- report

    private void Report(Recipe recipe, Totals t)
    {
        Log($"--- parity table {recipe.Name} (points compared {t.Points}, site-influenced and excluded {t.Sites}) ---");
        var rows = new List<Dictionary<string, object>>();
        void Row(string group, string field, string unit, double scale, double? p999Tol, double? maxTol, double? meanTol)
        {
            if (!t.Accs.TryGetValue(group + ":" + field, out Acc? a)) return;
            Log($"{recipe.Name} {group,-8} {field,-15} {AccRow(a, unit, scale)}");
            rows.Add(new() { ["group"] = group, ["field"] = field, ["n"] = a.Count, ["p99_9"] = a.Percentile(0.999) * scale, ["max"] = a.Max * scale, ["mean"] = a.Mean * scale, ["exact_percent"] = a.ExactPercent, ["worst"] = a.Worst });
            if (p999Tol is { } p && a.Percentile(0.999) * scale > p) _failures.Add($"{recipe.Name} {group} {field}: p99.9 {a.Percentile(0.999) * scale:E3} > {p}");
            if (maxTol is { } m && a.Max * scale > m) _failures.Add($"{recipe.Name} {group} {field}: max {a.Max * scale:E3} > {m} at {a.Worst}");
            if (meanTol is { } e && a.Mean * scale > e) _failures.Add($"{recipe.Name} {group} {field}: mean {a.Mean * scale:E3} > {e}");
        }
        foreach (string group in Spacings.Select(s => $"S{s}").Concat(new[] { "random" }))
        {
            Row(group, "height", " m", 1d, HeightP999, HeightMax, HeightMean);
            if (group != "random") Row(group + "c", "height", " m", 1d, HeightP999, HeightMax, HeightMean);
            Row(group, "weights_255", "/255", 1d, null, WeightMax255, null);
            Row(group, "continentalness", "", 1d, null, 0.00784, null);
            Row(group, "ridge", "", 1d, null, 0.00784, null);
            Row(group, "erosion", "", 1d, null, 0.00784, null);
            Row(group, "temperature", "", 1d, null, 0.00784, null);
            Row(group, "moisture", "", 1d, null, 0.00784, null);
        }
        double flipFraction = t.CategoricalCompared == 0 ? 0 : (double)t.CategoricalFlips / t.CategoricalCompared;
        Log($"{recipe.Name} water: ocean points={t.WaterPoints} flips={t.WaterFlips} flips with |h|>=0.05 m: {t.WaterFlipsBad} (largest flip |h|={t.WorstFlipHeight:F5} m {t.WorstFlip})");
        Log($"{recipe.Name} biome categorical flips: {t.CategoricalFlips} of {t.CategoricalCompared} ({flipFraction * 100:F4}%)");
        if (t.WaterFlipsBad != 0) _failures.Add($"{recipe.Name}: {t.WaterFlipsBad} water-kind flips with |h| >= 0.05 m (worst {t.WorstFlip})");
        if (flipFraction > FlipFraction) _failures.Add($"{recipe.Name}: categorical biome flips {flipFraction * 100:F4}% > 0.1%");
        _json[recipe.Name] = new Dictionary<string, object>
        {
            ["points_compared"] = t.Points, ["site_excluded"] = t.Sites, ["rows"] = rows,
            ["water_flips"] = t.WaterFlips, ["water_flips_bad"] = t.WaterFlipsBad, ["water_points"] = t.WaterPoints,
            ["biome_flip_fraction"] = flipFraction,
        };
    }

    // ---------------------------------------------------------------- timings

    private void RunTimings(ProxyGpuHeightKernel kernel, List<Position> positions)
    {
        Log("=== K1 timings (GPU timestamps; local RenderingDevice; median of 15 after a 1.5 s clock warm-up and 5 warm-up dispatches) ===");
        string[] wanted = { "hill_country#0", "plateau_edge#0", "mountain_gullies#0", "deep_basin#0" };
        var rows = new List<Dictionary<string, object>>();
        const int Batch = 32;
        // The first dispatches after idle run at low GPU clocks; hold the GPU busy before measuring.
        var warm = System.Diagnostics.Stopwatch.StartNew();
        Position? warmPos = positions.Cast<Position?>().FirstOrDefault(p => p!.Value.Label == "hill_country#0");
        if (warmPos is { } wp)
        {
            SectorJob wj = MakeSector(wp, 8);
            long wa = ((wj.Origin % Circumference) + Circumference) % Circumference;
            while (warm.Elapsed.TotalSeconds < 1.5) kernel.TimeLattice(wa, Circumference, -8, wj.OriginAcross - 8, 8, Window, Window, 256, 512, true);
        }
        foreach (string label in wanted)
        {
            Position? pos = positions.Cast<Position?>().FirstOrDefault(p => p!.Value.Label == label);
            if (pos is null) { Log($"timing fixture {label} not found"); continue; }
            foreach (int spacing in Spacings)
            {
                SectorJob j = MakeSector(pos.Value, spacing);
                long anchor = ((j.Origin % Circumference) + Circumference) % Circumference;
                int size = Cells * spacing, biomeSide = size / 16;
                (double gpu, double wall) Median(Func<ProxyGpuHeightKernel.RunTiming> run)
                {
                    for (int i = 0; i < 5; i++) run();
                    var g = new List<double>(); var wl = new List<double>();
                    for (int i = 0; i < 15; i++) { var t = run(); g.Add(t.GpuMs); wl.Add(t.WallMs); }
                    g.Sort(); wl.Sort();
                    return (g[7], wl[7]);
                }
                var h1 = Median(() => kernel.TimeLattice(anchor, Circumference, -spacing, j.OriginAcross - spacing, spacing, Window, Window, 1, 0, false));
                var b1 = Median(() => kernel.TimeLattice(anchor, Circumference, 8, j.OriginAcross + 8, 16, biomeSide, biomeSide, 1, 0, true));
                var hN = Median(() => kernel.TimeLattice(anchor, Circumference, -spacing, j.OriginAcross - spacing, spacing, Window, Window, Batch, size, false));
                var bN = Median(() => kernel.TimeLattice(anchor, Circumference, 8, j.OriginAcross + 8, 16, biomeSide, biomeSide, Batch, size, true));
                double halo = -1, haloWall = -1;
                if (spacing == 2)
                {
                    var hh = Median(() => kernel.TimeLattice(anchor, Circumference, -2, j.OriginAcross - 2, 2, 129, 129, 1, 0, false));
                    halo = hh.gpu; haloWall = hh.wall;
                }
                Log($"timing {label,-20} S={spacing,-2} size={size,-4}m  height 67x67: {h1.gpu:F3} ms gpu ({h1.wall:F3} wall)  biome {biomeSide}x{biomeSide}: {b1.gpu:F3} ms gpu ({b1.wall:F3} wall)  " +
                    $"= {h1.gpu + b1.gpu:F3} ms/sector alone | batch {Batch}: height {hN.gpu / Batch:F3} + biome {bN.gpu / Batch:F3} = {(hN.gpu + bN.gpu) / Batch:F3} ms/sector" +
                    (halo >= 0 ? $" | 2 m shoreline halo 129x129: {halo:F3} ms gpu ({haloWall:F3} wall)" : ""));
                rows.Add(new()
                {
                    ["fixture"] = label, ["spacing"] = spacing, ["size_m"] = size, ["height_67_gpu_ms"] = h1.gpu, ["height_67_wall_ms"] = h1.wall,
                    ["biome_side"] = biomeSide, ["biome_gpu_ms"] = b1.gpu, ["biome_wall_ms"] = b1.wall, ["batch"] = Batch,
                    ["batch_height_gpu_ms_per_sector"] = hN.gpu / Batch, ["batch_biome_gpu_ms_per_sector"] = bN.gpu / Batch,
                    ["halo_129_gpu_ms"] = halo, ["halo_129_wall_ms"] = haloWall,
                });
            }
        }
        _json["timings"] = rows;
    }
}
