using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-9 (docs/work-orders/terrain-vegetation/T2.md, reset doc section 5): the production river-graph lifecycle on the whole ring, three times: a cold bake (no file), a warm load (the file the first bake wrote), a second cold bake.
/// Each runs <see cref="RiverGraphStore.GetOrBuild"/>, so it measures what a loading screen waits for. Arguments after `--`: --source=gpu|cpu (default gpu), --seed=1337, --label=&lt;text&gt;.
/// Prints per-stage seconds, file bytes, worker count and the machine name, and writes them to .godot/audit_baseline/&lt;machine&gt;/t2r/bench/&lt;stamp&gt;/. It fails on errors, never on time.
/// GPU runs need a windowed Vulkan run. Prints DRAINAGE_BAKE_BENCHMARK=PASS|FAIL. Times are wall clock on a machine that may be in use.
/// </summary>
public partial class DrainageBakeBenchmark : RefCounted
{
    private static string Arg(string name, string fallback)
    {
        string prefix = "--" + name + "=";
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return fallback;
    }

    public int Run()
    {
        string work = ProjectSettings.GlobalizePath("res://.godot/benchmarks/drainage_bake");
        try
        {
            string sourceName = Arg("source", "gpu");
            int seed = int.Parse(Arg("seed", "1337"), System.Globalization.CultureInfo.InvariantCulture);
            string label = Arg("label", "");
            HydrologySourcePreference preference = sourceName == "cpu" ? HydrologySourcePreference.Cpu : HydrologySourcePreference.Gpu;
            TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(seed, 1, new[] { "water.rivers" });
            using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(seed, recipe);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            HydrologyBakeInputs inputs = HydrologyBakeInputs.Capture(settings, recipe, HydrologyBakeInputs.DefaultWorkers, preference, work);
            string device = RenderingServer.GetRenderingDevice()?.GetDeviceName() ?? "none (headless)";
            string machine = System.Environment.MachineName;
            Console.WriteLine($"DRAINAGE_BAKE_BENCHMARK_DETAIL machine={machine} device={device} source={sourceName} workers={inputs.Workers} processors={System.Environment.ProcessorCount}");

            string path = inputs.FilePath(inputs.Identity);
            var rows = new (string Phase, HydrologyBakeStats Stats)[3];
            for (int i = 0; i < 3; i++)
            {
                RiverGraphStore.Clear();
                if (i != 1 && File.Exists(path)) File.Delete(path);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                RiverGraph graph = RiverGraphStore.GetOrBuild(inputs);
                double wall = clock.Elapsed.TotalSeconds;
                HydrologyBakeStats stats = RiverGraphStore.LastStats ?? throw new InvalidOperationException("no stats after a build");
                if (i == 1 != stats.Loaded) throw new InvalidOperationException(i == 1 ? "the warm run baked instead of loading the file" : "a cold run loaded a file");
                if (graph.InputIdentity != inputs.Identity) throw new InvalidOperationException("the graph does not carry the input identity");
                rows[i] = (i == 0 ? "cold bake" : i == 1 ? "warm load" : "second cold bake", stats with { TotalSeconds = wall });
                Console.WriteLine($"DRAINAGE_BAKE_BENCHMARK_DETAIL {rows[i].Phase}: {Describe(rows[i].Stats)}");
            }

            var text = new StringBuilder();
            text.AppendLine($"machine {machine}, device {device}, requested source {sourceName}, workers {inputs.Workers}, seed {seed}. Wall-clock seconds on a machine that may have been in use: indicative only.");
            text.AppendLine("| Phase | Source | Total | Grid | Barriers | Noise | Conditioning | Routing | Graph | Pack | Write | Load | File MiB |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach ((string phase, HydrologyBakeStats s) in rows)
                text.AppendLine(FormattableString.Invariant($"| {phase} | {s.Source} | {s.TotalSeconds:F2} | {s.GridSeconds:F2} | {s.BarrierSeconds:F2} | {s.NoiseSeconds:F2} | {s.ConditioningSeconds:F2} | {s.RoutingSeconds:F2} | {s.GraphSeconds:F2} | {s.PackSeconds:F2} | {s.WriteSeconds:F2} | {s.LoadSeconds:F2} | {s.FileBytes / 1048576d:F2} |"));
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + (label.Length == 0 ? "" : "_" + label);
            string dir = ProjectSettings.GlobalizePath($"res://.godot/audit_baseline/{machine}/t2r/bench/{stamp}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "benchmark.txt"), text.ToString());
            File.WriteAllText(Path.Combine(dir, "benchmark.json"), System.Text.Json.JsonSerializer.Serialize(new { machine, device, source = sourceName, workers = inputs.Workers, rows = rows.Select(r => new { r.Phase, r.Stats }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (string line in text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_BENCHMARK_DETAIL " + line);
            Console.WriteLine("DRAINAGE_BAKE_BENCHMARK_DETAIL dir=" + dir);
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_BAKE_BENCHMARK=FAIL exception " + e); return 1; }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
        Console.WriteLine("DRAINAGE_BAKE_BENCHMARK=PASS");
        return 0;
    }

    private static string Describe(HydrologyBakeStats s)
        => FormattableString.Invariant($"source={s.Source} total={s.TotalSeconds:F2}s grid={s.GridSeconds:F2} barriers={s.BarrierSeconds:F2} noise={s.NoiseSeconds:F2} conditioning={s.ConditioningSeconds:F2} routing={s.RoutingSeconds:F2} graph={s.GraphSeconds:F2} pack={s.PackSeconds:F2} write={s.WriteSeconds:F2} load={s.LoadSeconds:F2} bytes={s.FileBytes}");
}
