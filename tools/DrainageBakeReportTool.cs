using System;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-4 (docs/work-orders/terrain-vegetation/T2.md): the drainage bake report tool. Runs the production height source (GPU or CPU), the barriers,
/// conditioning, routing and accumulation on the whole ring or a strip, writes the report (text and JSON) and the river map under
/// .godot/audit_baseline/&lt;machine&gt;/t2r/bake/&lt;stamp&gt;/. Arguments after `--`: --source=gpu|cpu (default gpu), --ring (default) or --strip=&lt;start m&gt;,&lt;km&gt;,
/// --seed=1337, --noise=6 (m), --lake-abundance=&lt;0..1&gt; (default: the recipe's 0.5), --crop=&lt;start km&gt;,&lt;km&gt; (default 10134,300), --rim-crop=&lt;start km&gt;,&lt;km&gt;, --evap-sweep (T2R-6b), --danger and --danger-grid (T3R-1; --no-danger-maps), --part-hashes=<graph file> (T3R-3), --site-intakes (the option) or --intake-report (the measurement alone, today's rule) with an optional --keep-out=<m> (T2R-19; DrainageBakeReportTool.SiteIntakes.cs), --terminals with an optional --near=&lt;around km&gt;,&lt;across km&gt;,&lt;radius km&gt; (T2R-6c and T2R-17; DrainageBakeReportTool.Terminals.cs), --label=&lt;text&gt;, --no-maps.
/// GPU runs need a windowed Vulkan run (not --headless). Prints DRAINAGE_BAKE_REPORT=PASS|FAIL.
/// </summary>
public partial class DrainageBakeReportTool : RefCounted
{
    private static string Arg(string name, string fallback)
    {
        string prefix = "--" + name + "=";
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return fallback;
    }

    private static bool Flag(string name) => OS.GetCmdlineUserArgs().Contains("--" + name);

    public int Run()
    {
        RenderingDevice? rd = null;
        try
        {
            string partHashFile = Arg("part-hashes", "");
            if (partHashFile.Length > 0)
            {
                Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL stored part hashes (FNV-1a) of " + partHashFile + ": " + string.Join(", ", RiverGraphFormat.PartHashes(File.ReadAllBytes(partHashFile)).Select(p => $"{p.Part} {p.Hash:X16}")));
                Console.WriteLine("DRAINAGE_BAKE_REPORT=PASS");
                return 0;
            }
            string sourceName = Arg("source", "gpu");
            int seed = int.Parse(Arg("seed", "1337"), System.Globalization.CultureInfo.InvariantCulture);
            double noise = double.Parse(Arg("noise", DrainageBake.DefaultRoutingNoiseMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
            string abundanceText = Arg("lake-abundance", "");
            double? abundance = abundanceText.Length == 0 ? null : double.Parse(abundanceText, System.Globalization.CultureInfo.InvariantCulture);
            string[] crop = Arg("crop", "10134,300").Split(',');
            double cropStartKm = double.Parse(crop[0], System.Globalization.CultureInfo.InvariantCulture), cropKm = double.Parse(crop[1], System.Globalization.CultureInfo.InvariantCulture);
            string label = Arg("label", "");
            int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 16);

            TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(seed, 1, new[] { "water.rivers" });
            DrainageGridSpec spec;
            string scope = Arg("strip", "");
            if (scope.Length == 0) spec = DrainageGridSpec.Ring(recipe);
            else
            {
                string[] parts = scope.Split(',');
                spec = DrainageGridSpec.Strip(recipe, double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            }

            using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(seed, recipe);
            DrainageClimateInputs climate = HydrologyBakeInputs.Capture(settings, recipe, workers).Climate;
            IDrainageHeightSource source;
            if (sourceName == "cpu") source = new CpuDrainageHeightSource(recipe, climate, workers);
            else
            {
                rd = RenderingServer.CreateLocalRenderingDevice();
                if (rd == null) { Console.Error.WriteLine("DRAINAGE_BAKE_REPORT=FAIL no local RenderingDevice (run windowed, not --headless)"); return 1; }
                Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL device: {rd.GetDeviceName()}");
                source = new GpuDrainageHeightSource(rd, recipe, climate);
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
            double gridSeconds = clock.Elapsed.TotalSeconds;
            DrainageField field = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None, new DrainageBakeOptions { RoutingNoiseMeters = noise, LakeAbundance = abundance, SiteIntakes = Flag("site-intakes"), KeepOutMeters = Arg("keep-out", "").Length == 0 ? null : double.Parse(Arg("keep-out", ""), System.Globalization.CultureInfo.InvariantCulture) });
            DrainageBakeReport report = field.Report with { GridSeconds = gridSeconds };

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + (label.Length == 0 ? "" : "_" + label);
            string dir = ProjectSettings.GlobalizePath($"res://.godot/audit_baseline/{System.Environment.MachineName}/t2r/bake/{stamp}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bake_report.txt"), $"source {source.Name}, seed {seed}, routing noise {noise} m\n" + report.ToText());
            File.WriteAllText(Path.Combine(dir, "bake_report.json"), report.ToJson());
            Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL source={source.Name} seed={seed} noise={noise} dir={dir}");
            foreach (string line in report.ToText().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
            if (Flag("evap-sweep"))
            {
                double baseAbundance = abundance ?? (recipe.HasRivers ? recipe.GetExtensionField("lake_abundance") : 0.5d);
                var sweep = new System.Text.StringBuilder();
                sweep.AppendLine(FormattableString.Invariant($"evap sweep at E0 {DrainageLakeBalance.E0:F1} (routing rerun on the same conditioning; breaching does not depend on lake_abundance)"));
                sweep.AppendLine("| lake_abundance | lambda | flooded share of land | closed share of basins | depth p99 (m) | lakes | dry floor share of land | balance s |");
                sweep.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (double la in new[] { 0d, 0.25d, 0.5d, 0.75d, 1d })
                {
                    DrainageBakeReport row = DrainageBake.Reroute(field, la, 1d, workers).Report;
                    sweep.AppendLine(FormattableString.Invariant($"| {la:F2} | {row.BalanceLambda:F3} | {row.LakeShareOfLand:P2} | {(double)row.BasinsClosed / Math.Max(1, row.BasinsClosed + row.BasinsSpilling):P1} | {row.LakeDepthP99:F1} | {row.LakeCount} | {row.DryShareOfLand:P2} | {row.BalanceSeconds:F2} |"));
                }
                sweep.AppendLine(FormattableString.Invariant($"E0 candidates at lake_abundance {baseAbundance:F2} (target 8 % flooded, band 6-10 %)"));
                sweep.AppendLine("| E0 | flooded share of land | closed share of basins | depth p99 (m) | lakes |");
                sweep.AppendLine("|---|---|---|---|---|");
                foreach (double e0 in new[] { 6d, 8d, 10d, 12d, 15d, 20d })
                {
                    DrainageBakeReport row = DrainageBake.Reroute(field, baseAbundance, e0 / DrainageLakeBalance.E0, workers).Report;
                    sweep.AppendLine(FormattableString.Invariant($"| {e0:F0} | {row.LakeShareOfLand:P2} | {(double)row.BasinsClosed / Math.Max(1, row.BasinsClosed + row.BasinsSpilling):P1} | {row.LakeDepthP99:F1} | {row.LakeCount} |"));
                }
                File.WriteAllText(Path.Combine(dir, "evap_sweep.txt"), sweep.ToString());
                foreach (string line in sweep.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
            }
            if (!Flag("no-rivers"))
            {
                bool diag = Flag("diag");
                RiverNetwork net = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { Source = source, Workers = workers, Levels = !Flag("no-levels"), KeepStations = diag, WanderScale = double.Parse(Arg("wander-scale", "1"), System.Globalization.CultureInfo.InvariantCulture), MeanderScale = double.Parse(Arg("meander-scale", "1"), System.Globalization.CultureInfo.InvariantCulture), EaseHairpins = Arg("ease-hairpins", "1") != "0", Smooth = Arg("smooth", "1") != "0", Snap = Arg("snap", "0") != "0" }, CancellationToken.None, out RiverBuildReport rivers);
                File.WriteAllText(Path.Combine(dir, "river_report.txt"), rivers.ToText());
                foreach (string line in rivers.ToText().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                string window = Arg("window", "");
                if (window.Length > 0)
                {
                    // T2R-19 picture: --window=<around km>,<across km>,<size km> writes a square map window with the curves (barrier cells violet, intake cells orange).
                    string[] w = window.Split(',');
                    string windowPath = RiverMapWriter.WriteWindow(field, grid, net, Path.Combine(dir, "site_window.png"), double.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture) * 1000d, double.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) * 1000d, double.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture), double.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture), 40d);
                    Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL site window=" + windowPath);
                }
                if (Flag("site-intakes") || Flag("intake-report"))
                {
                    // T2R-19: the keep-out measurement (read-only; reads no spans, so it runs with --no-levels).
                    string intakeText = SiteIntakeReport(net, field, grid, recipe, rivers, Flag("site-intakes"), Arg("keep-out", "").Length == 0 ? DrainageBarriers.DefaultKeepOutMeters : double.Parse(Arg("keep-out", ""), System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText(Path.Combine(dir, "site_intakes.txt"), intakeText);
                    foreach (string line in intakeText.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                }
                if (Flag("no-levels") && (Flag("site-intakes") || Flag("intake-report")))
                {
                    // The graph wrap needs the levels; the T2R-19 measurement runs without them and ends here.
                    (source as IDisposable)?.Dispose();
                    Console.WriteLine("DRAINAGE_BAKE_REPORT=PASS");
                    return 0;
                }
                string graphText = GraphReport(net, field, grid, dir, sourceName == "cpu" ? (byte)0 : (byte)1, out RiverGraph graph);
                File.WriteAllText(Path.Combine(dir, "graph_report.txt"), graphText);
                foreach (string line in graphText.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                if (Flag("terminals"))
                {
                    // T2R-6c and T2R-17: how stems end and how water meets barrier cells (read-only; without the flag the report and its time are unchanged).
                    string terminalsText = TerminalsReport(net, graph, field, grid, source, recipe, workers, Arg("near", ""), dir);
                    File.WriteAllText(Path.Combine(dir, "terminals_report.txt"), terminalsText);
                    foreach (string line in terminalsText.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                }
                if (Flag("danger") || Flag("danger-grid")) DangerBlock(net, field, grid, dir, seed, recipe);
                if (diag && net.Stations != null)
                {
                    string diagText = RiverDiagnostics(net, field, grid);
                    File.WriteAllText(Path.Combine(dir, "river_diag.txt"), diagText);
                    foreach (string line in diagText.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                }
                if (Flag("d4"))
                {
                    RiverNetwork d4Net = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { ThresholdKm2 = 50d, Levels = false, Source = source, Workers = workers, Smooth = Arg("smooth", "1") != "0", Snap = Arg("snap", "0") != "0" }, CancellationToken.None, out _);
                    RiverNetwork d4Wander = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { ThresholdKm2 = 50d, Levels = false, Source = source, Workers = workers, MeanderScale = 0d, Smooth = Arg("smooth", "1") != "0", Snap = Arg("snap", "0") != "0" }, CancellationToken.None, out _);
                    RiverNetwork d4NoEase = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { ThresholdKm2 = 50d, Levels = false, Source = source, Workers = workers, EaseHairpins = false }, CancellationToken.None, out _);
                    RiverNetwork d4Base = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { ThresholdKm2 = 50d, Levels = false, Source = source, Workers = workers, MeanderScale = 0d, WanderScale = 0d, EaseHairpins = false, Smooth = false, Snap = false }, CancellationToken.None, out _);
                    PathSegment[] fixture = RiverPathMetrics.ReadFixtureSegments(ProjectSettings.GlobalizePath("res://tools/fixtures/hydrology/d4_baseline/graph.json.gz"), out PathGrid maskGrid);
                    string d4Text = "baseline (frozen fixture re-scored): " + RiverPathMetrics.Evaluate(fixture, maskGrid).ToText() + "\nproduction centrelines at 50 km^2: "
                        + RiverPathMetrics.Evaluate(RiverPathMetrics.FromNetwork(d4Net, spec.ColumnStart * spec.AroundStepMeters), maskGrid).ToText()
                        + "\nproduction, meander off (wander only): "
                        + RiverPathMetrics.Evaluate(RiverPathMetrics.FromNetwork(d4Wander, spec.ColumnStart * spec.AroundStepMeters), maskGrid).ToText()
                        + "\nproduction, hairpin easing off: " + RiverPathMetrics.Evaluate(RiverPathMetrics.FromNetwork(d4NoEase, spec.ColumnStart * spec.AroundStepMeters), maskGrid).ToText()
                        + "\nbase curve only (no wander, meander or easing): " + RiverPathMetrics.Evaluate(RiverPathMetrics.FromNetwork(d4Base, spec.ColumnStart * spec.AroundStepMeters), maskGrid).ToText();
                    File.WriteAllText(Path.Combine(dir, "d4.txt"), d4Text);
                    foreach (string line in d4Text.Split('\n')) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
                }
                if (!Flag("no-maps"))
                {
                    (string curveCrop, string closeUp) = RiverMapWriter.Write(field, grid, net, dir, "bake", cropStartKm * 1000d, cropKm * 1000d);
                    Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL curves crop={curveCrop} closeup={closeUp}");
                }
            }
            (source as IDisposable)?.Dispose();
            if (!Flag("no-maps"))
            {
                var clockMaps = System.Diagnostics.Stopwatch.StartNew();
                (string overview, string cropPath, string raw) = DrainageMapWriter.Write(field, dir, "bake", cropStartKm * 1000d, cropKm * 1000d, spec.IsWholeRing);
                Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL maps seconds={clockMaps.Elapsed.TotalSeconds:F1} overview={overview} crop={cropPath} raw={raw}");
                string rimText = Arg("rim-crop", "");
                if (rimText.Length > 0)
                {
                    string[] rim = rimText.Split(',');
                    (_, string rimPath, _) = DrainageMapWriter.Write(field, dir, "rim", double.Parse(rim[0], System.Globalization.CultureInfo.InvariantCulture) * 1000d, double.Parse(rim[1], System.Globalization.CultureInfo.InvariantCulture) * 1000d, false);
                    Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL rim crop={rimPath}");
                }
            }
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_BAKE_REPORT=FAIL exception " + e); return 1; }
        finally { rd?.Free(); }
        Console.WriteLine("DRAINAGE_BAKE_REPORT=PASS");
        return 0;
    }

    private static readonly (byte R, byte G, byte B)[] DangerColors = { (51, 115, 242), (38, 191, 89), (250, 153, 26), (230, 26, 26) };

    /// <summary>T3R-1: --danger re-buckets danger on the baked network (report, JSON, maps, staircase data); --danger-grid prints the 36-combination grid of reset doc 4.5. The graph is not changed.</summary>
    private static void DangerBlock(RiverNetwork net, DrainageField field, DrainageGrid grid, string dir, int seed, TerrainGenerationParameters recipe)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RiverDangerSpans spans = RiverDangerReport.Cut(net);
        double cutSeconds = clock.Elapsed.TotalSeconds;
        var text = new System.Text.StringBuilder();
        text.AppendLine(FormattableString.Invariant($"span cut {cutSeconds:F2} s"));
        if (Flag("danger"))
        {
            text.Append(RiverDangerReport.StoredReport(net, RiverExclusions.Build(recipe), out string storedJson));
            File.WriteAllText(Path.Combine(dir, "danger_report_production.json"), storedJson);
            text.Append(RiverDangerReport.Report(net, spans, RiverDangerConstants.Default, out string json));
            File.WriteAllText(Path.Combine(dir, "danger_report.json"), json);
        }
        if (Flag("danger-grid"))
        {
            clock.Restart();
            text.Append(RiverDangerReport.Grid(net, spans, out var rows));
            text.AppendLine(FormattableString.Invariant($"grid evaluation {clock.Elapsed.TotalSeconds:F1} s for {rows.Count} combinations; inside the window: {rows.Count(r => RiverDangerReport.InWindow(r.Share))}"));
            if (net.SpanTable != null)
            {
                clock.Restart();
                text.Append(RiverDangerReport.StoredGrid(net, RiverExclusions.Build(recipe), out var stored));
                text.AppendLine(FormattableString.Invariant($"production grid evaluation {clock.Elapsed.TotalSeconds:F1} s for {stored.Count} combinations; inside the window: {stored.Count(r => RiverDangerReport.InWindow(r.Share))}"));
            }
        }
        File.WriteAllText(Path.Combine(dir, "danger_report.txt"), text.ToString());
        foreach (string line in text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL " + line);
        if (!Flag("danger") || Flag("no-danger-maps")) return;
        RiverDangerResult result = RiverDangerReport.Evaluate(net, spans, RiverDangerConstants.Default, true);
        RiverMapWriter.RiverMapColor ColorAt(int stem, double s)
        {
            // T3R-3: the production map reads the stored spans (landings and exclusions included).
            RiverSpanTable table = net.SpanTable!;
            int i = table.SpanAt(net.ReachAt(stem, s), s);
            return new RiverMapWriter.RiverMapColor(DangerColors[(int)table.Class[i]], table.Spans[i].Landing > 1f, (int)table.Class[i], table.HalfWidth[i]);
        }
        string[] crop = Arg("crop", "10134,300").Split(',');
        double cropStartKm = double.Parse(crop[0], System.Globalization.CultureInfo.InvariantCulture), cropKm = double.Parse(crop[1], System.Globalization.CultureInfo.InvariantCulture);
        clock.Restart();
        string overview = RiverMapWriter.WriteOverview(field, grid, net, Path.Combine(dir, "danger_map_overview_raw.png"), ColorAt);
        (string cropPath, _) = RiverMapWriter.Write(field, grid, net, dir, "danger", cropStartKm * 1000d, cropKm * 1000d, ColorAt, false);
        File.Move(cropPath, Path.Combine(dir, "danger_map_crop_raw.png"), true);
        File.WriteAllText(Path.Combine(dir, "danger_map_legend.txt"),
            "Danger map (T3R-3, production: the stored spans of the graph, at most 2 cells each). River colour by danger class of each span, after landings and exclusions: blue calm (Vd < 1.5 m/s), green swift (< 4), orange fierce (< 7), red torrent. "
            + "White outline: a landing (a span softened by the 5 km exit rule). Line width is the span's modelled width, at least 1 px. Hillshade of the valley floor, dimmed; dark blue sea, light blue lake, tan dry basin floor, violet barrier. "
            + $"Overview: the whole ring as 8 strips of 3927 km stacked top to bottom, around to the right, across downward, 1 px per km. Crop: around {cropStartKm:F0} km, {cropKm:F0} km wide, 6 px per km.\n");
        Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL danger maps seconds={clock.Elapsed.TotalSeconds:F1} overview={overview} crop={Path.Combine(dir, "danger_map_crop_raw.png")}");
        int pick = RiverDangerReport.PickStaircase(net, spans, result);
        if (pick >= 0)
        {
            File.WriteAllText(Path.Combine(dir, "danger_staircase.csv"), RiverDangerReport.StaircaseCsv(net, spans, result, pick));
            Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL staircase span {pick} stem {spans.Stem[pick]} csv={Path.Combine(dir, "danger_staircase.csv")}");
        }
        else Console.WriteLine("DRAINAGE_BAKE_REPORT_DETAIL staircase: no span matched");
        (int steepStem, double steepMid) = RiverDangerReport.PickSteepWindow(net);
        if (steepStem >= 0)
        {
            File.WriteAllText(Path.Combine(dir, "steep_window.csv"), RiverDangerReport.SteepWindowCsv(net, steepStem, steepMid));
            Console.WriteLine($"DRAINAGE_BAKE_REPORT_DETAIL steep window stem {steepStem} s {steepMid:F0} csv={Path.Combine(dir, "steep_window.csv")}");
        }
    }

    /// <summary>T2R-6: wraps the network into the graph, writes the file, reads it back and prints counts, bytes by part, the round trip and the lake statistics (the deepest five).</summary>
    private static string GraphReport(RiverNetwork net, DrainageField field, DrainageGrid grid, string dir, byte source, out RiverGraph built)
    {
        var sb = new System.Text.StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RiverGraph graph = RiverGraph.Build(net, field, grid, 0, source);
        built = graph;
        double buildSeconds = clock.Elapsed.TotalSeconds;
        byte[] bytes = RiverGraphFormat.Write(graph);
        File.WriteAllBytes(Path.Combine(dir, $"river_graph_v{HydrologyVersions.GraphFormat}.bin"), bytes);
        clock.Restart();
        long before = GC.GetTotalMemory(true);
        bool ok = RiverGraphFormat.TryRead(bytes, null, out RiverGraph? loaded, out string error);
        double loadSeconds = clock.Elapsed.TotalSeconds;
        long retained = GC.GetTotalMemory(true) - before;
        bool equal = ok && loaded != null && loaded.IdentityHash == graph.IdentityHash && RiverGraphFormat.Write(loaded).AsSpan().SequenceEqual(bytes);
        sb.AppendLine(FormattableString.Invariant($"graph: format {HydrologyVersions.GraphFormat}, drainage algorithm {HydrologyVersions.DrainageAlgorithm}, source {(source == 0 ? "cpu" : "gpu")}, nodes {net.NodeAroundDm.Length}, stems {net.Stems.Length}, reaches {net.Reaches.Length}, knots {net.Knots.Count}, lakes {graph.Lakes.Length}, inlets {graph.Inlets.Length}, lake runs {graph.Runs.Length}, bucket entries {graph.Index.EntryCount}"));
        sb.AppendLine(FormattableString.Invariant($"graph identity hash {graph.IdentityHash:X16}; file {bytes.Length} bytes ({bytes.Length / 1048576d:F2} MiB); wrap {buildSeconds:F2} s, load and index {loadSeconds:F2} s; read-back {(ok ? "ok" : "FAILED " + error)}, byte-equal re-serialise and same hash {equal}"));
        sb.AppendLine("stored bytes by part: " + string.Join(", ", graph.StoredBytesByPart().Select(p => $"{p.Part} {p.Bytes}")));
        sb.AppendLine("stored part hashes (FNV-1a): " + string.Join(", ", RiverGraphFormat.PartHashes(bytes).Select(p => $"{p.Part} {p.Hash:X16}")));
        sb.AppendLine(FormattableString.Invariant($"resident estimate {graph.ResidentBytes} bytes ({graph.ResidentBytes / 1048576d:F2} MiB) by part: ") + string.Join(", ", graph.ResidentBytesByPart().Select(p => $"{p.Part} {p.Bytes}")));
        sb.AppendLine(FormattableString.Invariant($"measured GC growth while loading the graph: {retained} bytes ({retained / 1048576d:F2} MiB)"));
        // Lakes.
        double cellArea = field.CellAreaKm2;
        long lakeCells = graph.Lakes.Sum(l => (long)l.CellCount), landCells = 0;
        for (int c = 0; c < field.Count; c++) if (field.Kind[c] == DrainageCellKind.Land) landCells++;
        int noOutlet = graph.Lakes.Count(l => l.OutletStem < 0);
        double[] depths = graph.Lakes.Select(l => (double)l.Depth).OrderBy(d => d).ToArray();
        string Pct(double q) => depths.Length == 0 ? "-" : depths[Math.Min(depths.Length - 1, (int)(q * depths.Length))].ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        sb.AppendLine(FormattableString.Invariant($"lakes: count {graph.Lakes.Length}, flooded cells {lakeCells} ({lakeCells * cellArea:F0} km^2, {(double)lakeCells / Math.Max(1, landCells):P2} of land cells, {(double)lakeCells / Math.Max(1, field.Count):P2} of all cells), without an outlet river {noOutlet}; depth (level minus lowest cell) p50/p90/p99/max {Pct(0.5)}/{Pct(0.9)}/{Pct(0.99)}/{Pct(1)} m; over 100 m {graph.Lakes.Count(l => l.Depth > 100)}, over 500 m {graph.Lakes.Count(l => l.Depth > 500)}"));
        long dryCells = graph.Basins.Sum(b => (long)b.DryCellCount);
        int dryBad = 0;
        var dryRng = new Random(5150);
        for (int i = 0; i < 50_000; i++)
        {
            int col = dryRng.Next(field.Width), row = dryRng.Next(field.Height);
            if (graph.DryBasinAt(grid.Spec.RingColumn(col), row) >= 0 != field.IsDryFloor(row * field.Width + col)) dryBad++;
        }
        sb.AppendLine(FormattableString.Invariant($"basins: closed {graph.Basins.Length}, dry floor {dryCells} cells ({dryCells * cellArea:F0} km^2, {(double)dryCells / Math.Max(1, landCells):P2} of land cells) in {graph.DryRuns.Length} runs; DryBasinAt disagrees with the field on {dryBad} of 50000 random cells; lakes of closed basins {graph.Lakes.Count(l => l.Basin >= 0)}, spilling {graph.Lakes.Count(l => l.Basin < 0)}"));
        sb.AppendLine(FormattableString.Invariant($"stems: {net.Stems.Count(s => s.Terminal == RiverTerminal.Lake)} end in a lake, {net.Stems.Count(s => s.Terminal == RiverTerminal.Dead)} end on a dry pan or nowhere (Dead)"));
        int rank = 0;
        foreach (int i in Enumerable.Range(0, graph.Lakes.Length).OrderByDescending(i => graph.Lakes[i].Depth).Take(5))
        {
            RiverLake l = graph.Lakes[i];
            double aroundKm = (l.DeepestCol + 0.5) * graph.AroundStepMeters / 1000d, acrossKm = (l.DeepestRow + 0.5) * graph.AcrossStepMeters / 1000d;
            sb.AppendLine(FormattableString.Invariant($"deepest lake {++rank}: id {i}, depth {l.Depth:F0} m, level {l.Level:F0} m (raw {l.RawLevel:F1}), deepest cell col {l.DeepestCol} row {l.DeepestRow} (around {aroundKm:F0} km, across {acrossKm:F0} km) at {l.MinHeight:F1} m, area {l.CellCount * cellArea:F0} km^2 ({l.CellCount} cells), bounds cols {l.MinCol}-{l.MaxCol} rows {l.MinRow}-{l.MaxRow}, inlets {l.InletCount}, outlet stem {l.OutletStem}"));
        }
        return sb.ToString();
    }

    /// <summary>T2R-5b diagnostics (--diag): the deepest cut per stem with what set its level, cut-over-40 length by cause, and the level-profile slope per reach.</summary>
    private static string RiverDiagnostics(RiverNetwork net, DrainageField field, DrainageGrid grid)
    {
        var sb = new System.Text.StringBuilder();
        DrainageGridSpec spec = grid.Spec;
        var worst = new System.Collections.Generic.List<(double Cut, int Stem, int J, int J0)>();
        double over40 = 0, total = 0, over40Fill = 0, over40Rim = 0, over40Bank = 0, over40Tributary = 0, over40Lake = 0;
        float[] lowest = field.Lowest.ToArray();
        int[] lakeId = field.LakeId.ToArray();
        int CellAt(double around, double across)
        {
            double wrapped = net.WrapAround(around);
            int col = (int)Math.Floor(wrapped / spec.AroundStepMeters) - spec.ColumnStart, row = Math.Clamp((int)Math.Floor(across / spec.AcrossStepMeters), 0, spec.Rows - 1);
            if (col < 0) col += spec.RingColumns;
            return col >= 0 && col < field.Width ? row * field.Width + col : -1;
        }
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStemStations st = net.Stations![i];
            int n = st.S.Length;
            double best = double.NegativeInfinity; int bj = -1, bj0 = -1;
            int setter = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == 0 || st.Profile[j] < st.Profile[j - 1] - 1e-4) setter = j;
                double span = 0.5 * ((j + 1 < n ? st.S[j + 1] : st.S[j]) - (j > 0 ? st.S[j - 1] : st.S[j]));
                int reach = net.ReachAt(i, st.S[j]);
                double cut = st.CentreGround[j] - (RiverLevel.AtStem(net, i, st.S[j]) - net.Sections[reach].Depth);
                total += span;
                if (cut > 40)
                {
                    over40 += span;
                    net.Centre(i, st.S[j], out double ar, out double ac, out _, out _);
                    int sr = setter;
                    if (st.Filled[j]) over40Fill += span;
                    else if (ac < 2000 || ac > net.WidthM - 2000) over40Rim += span;
                    else if (Math.Abs(st.Ground[sr] - 0.6 - st.Profile[sr]) > 0.05) over40Tributary += span;
                    else if (st.Ground[sr] < st.CentreGround[sr] - 20) over40Bank += span;
                    int cell = CellAt(ar, ac);
                    if (cell >= 0 && lakeId[cell] >= 0) over40Lake += span;
                }
                if (cut > best) { best = cut; bj = j; bj0 = setter; }
            }
            if (bj >= 0) worst.Add((best, i, bj, bj0));
        }
        sb.AppendLine(FormattableString.Invariant($"cut>40 share {over40 / total:P2}: on filled stations {over40Fill / total:P2}, within 2 km of a rim {over40Rim / total:P2}, level set upstream by a tributary/hold {over40Tributary / total:P2}, level set by a bank sample 20 m under the centre {over40Bank / total:P2}, centre point in a lake cell {over40Lake / total:P2}"));
        worst.Sort((a, b) => b.Cut.CompareTo(a.Cut));
        foreach ((double cut, int i, int j, int j0) in worst.Take(25))
        {
            RiverStemStations st = net.Stations![i];
            RiverStem stem = net.Stems[i];
            net.Centre(i, st.S[j], out double ar, out double ac, out _, out _);
            net.Centre(i, st.S[j0], out double ar0, out double ac0, out _, out _);
            int cell = CellAt(ar, ac), cell0 = CellAt(ar0, ac0);
            int reach = net.ReachAt(i, st.S[j]);
            sb.AppendLine(FormattableString.Invariant(
                $"cut {cut:F0} m stem {i} ({stem.Terminal}, len {net.Curves[i].Length:F0} m, upLake {net.Reaches[stem.ReachStart].UpstreamLake}) s {st.S[j]:F0} at ({net.WrapAround(ar):F0},{ac:F0}) centreG {st.CentreGround[j]:F1} lowG {st.Ground[j]:F1} profile {st.Profile[j]:F1} filled {st.Filled[j]} cellLowest {(cell >= 0 ? lowest[cell] : float.NaN):F1} cellKind {(cell >= 0 ? field.Kind[cell] : DrainageCellKind.StripOutlet)} lake {(cell >= 0 ? lakeId[cell] : -9)} depth {net.Sections[reach].Depth:F1} level {RiverLevel.AtStem(net, i, st.S[j]):F1} stored {RiverLevel.Stored(net, i, st.S[j]):F1} recv {net.ReceivingLevel[i]:F1} tailDrop {net.TailDrop[i]:F1} T {net.TailMeters[i]:F0} termId {stem.TerminalId} termLakeLevel {(stem.Terminal == RiverTerminal.Lake ? field.Lakes.Span[stem.TerminalId].Level : float.NaN):F1}")
                + FormattableString.Invariant($" | set at s {st.S[j0]:F0} ({net.WrapAround(ar0):F0},{ac0:F0}) centreG {st.CentreGround[j0]:F1} lowG {st.Ground[j0]:F1} profile {st.Profile[j0]:F1} cellLowest {(cell0 >= 0 ? lowest[cell0] : float.NaN):F1} kind {(cell0 >= 0 ? field.Kind[cell0] : DrainageCellKind.StripOutlet)} lake {(cell0 >= 0 ? lakeId[cell0] : -9)}"));
        }
        // Lake-terminal stems with the largest tail drop: the receiving lake and the stem's last cell and the cell it drains into.
        float[] route = field.RouteHeight.ToArray(), cutRoute = field.CutRouteHeight.ToArray(), filledH = field.FilledHeight.ToArray(), unperturbed = field.UnperturbedFilled.ToArray();
        int[] down = field.Down.ToArray();
        string CellText(int c) => c < 0 ? "-" : FormattableString.Invariant($"[r{c / field.Width} c{c % field.Width} kind {field.Kind[c]} lowest {lowest[c]:F1} route {route[c]:F1} cutRoute {cutRoute[c]:F1} filled {filledH[c]:F1} F {unperturbed[c]:F1} lake {lakeId[c]}]");
        foreach (int i in Enumerable.Range(0, net.Stems.Length).Where(i => net.Stems[i].Terminal == RiverTerminal.Lake).OrderByDescending(i => net.TailDrop[i]).Take(6))
        {
            RiverStem stem = net.Stems[i];
            DrainageLake lake = field.Lakes.Span[stem.TerminalId];
            int pitCount = 0, waterBelow = 0; double maxMember = double.NegativeInfinity;
            for (int c = 0; c < field.Count; c++) if (lakeId[c] == stem.TerminalId) { pitCount++; if (lowest[c] < lake.Level) waterBelow++; maxMember = Math.Max(maxMember, lowest[c]); }
            sb.AppendLine(FormattableString.Invariant($"tailDrop {net.TailDrop[i]:F0} m stem {i}: lake {lake.Id} level {lake.Level:F1} min {lake.MinHeight:F1} flooded {lake.FloodedCells} water {lake.WaterCells} (recount members {pitCount}, lowest below level {waterBelow}, highest member lowest {maxMember:F1}) pit {CellText(lake.PitCell)}; last {CellText(stem.LastCell)} -> {CellText(down[stem.LastCell])}"));
        }
        // Level-profile slope per reach (drop of the stored profile over the reach length) against the grid S, by length.
        var ratio = new System.Collections.Generic.List<(double V, double W)>();
        var levelSlope = new System.Collections.Generic.List<(double V, double W)>();
        for (int r = 0; r < net.Reaches.Length; r++)
        {
            RiverReach rr = net.Reaches[r];
            double len = rr.SEnd - rr.SStart;
            if (len < 500) continue;
            double drop = RiverLevel.Stored(net, rr.Stem, rr.SStart) - RiverLevel.Stored(net, rr.Stem, rr.SEnd);
            double ls = Math.Max(drop / len, 1e-4);
            levelSlope.Add((ls, len));
            ratio.Add((rr.S / ls, len));
        }
        string P(System.Collections.Generic.List<(double V, double W)> v)
        {
            v.Sort((a, b) => a.V.CompareTo(b.V));
            double tot = v.Sum(x => x.W), run = 0; var res = new double[3]; int k = 0; double[] q = { 0.1, 0.5, 0.9 };
            foreach (var x in v) { run += x.W; while (k < 3 && run >= q[k] * tot) res[k++] = x.V; }
            return FormattableString.Invariant($"{res[0]:G4}/{res[1]:G4}/{res[2]:G4}");
        }
        sb.AppendLine("level-profile slope per reach (>= 500 m) p10/p50/p90: " + P(levelSlope) + "; grid S / level slope p10/p50/p90: " + P(ratio));
        return sb.ToString();
    }
}
