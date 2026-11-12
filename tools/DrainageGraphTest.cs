using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-4 (docs/work-orders/terrain-vegetation/T2.md): barriers, conditioning, routing and accumulation on synthetic fixtures (bowl excavation, the 40 m and
/// 32-cell limits, an uncertified exit, the seam, blocked diagonals, flat crossing repair, conservation) and on the two 400 km CPU-source strips: no cycles,
/// every visited land cell reaches an outlet, the filled surface never rises downstream, cuts within the limits, no flow across a barrier or wall, a dry
/// neighbour of lake water has every sample at or above the lake level, no lake water touches lower water, enclosed land at most 1 %, and two runs and
/// 1 against N workers give the same hashes. Negative controls: a fabricated cycle, an undrained pit and a barrier leak are each flagged.
/// Prints DRAINAGE_GRAPH=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests drainage_graph_test.
/// </summary>
public partial class DrainageGraphTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }

    public int Run()
    {
        try
        {
            Fixtures();
            Strips();
            Rivers();
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_GRAPH=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("DRAINAGE_GRAPH=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("DRAINAGE_GRAPH=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixtures

    private static DrainageDomain Make(int width, int height, float[] heights, DrainageCellKind[] kinds, bool wrap = false, double step = 1000d)
        => new(width, height, wrap, step, step, kinds, heights, heights, Enumerable.Repeat(0.5f, heights.Length).ToArray());

    private static DrainageField Bake(DrainageDomain d, double abundance, int workers = 1)
        => DrainageBake.BakeDomain(null, d, abundance, workers, null, CancellationToken.None);

    private static DrainageDomain Bowl(float sill, float exit = 0f)
    {
        const int w = 5;
        var h = new float[15];
        new[] { 15f, 10f, sill, 12f, exit }.CopyTo(h, w);
        for (int i = 0; i < 5; i++) { h[i] = 1000; h[10 + i] = 1000; }
        var kind = Enumerable.Repeat(DrainageCellKind.Barrier, 15).ToArray();
        Array.Fill(kind, DrainageCellKind.Land, w, 4);
        kind[9] = DrainageCellKind.Ocean;
        return Make(w, 3, h, kind);
    }

    private void Fixtures()
    {
        // Fill-only bowl: two lake cells, level 18 (lowest bordering value), no cut.
        DrainageField fill = Bake(Bowl(18f), 1d);
        Check(fill.UnperturbedFilled[6] == 18f && fill.UnperturbedFilled[5] == 18f, "bowl: unperturbed filled surface F is not the lake level in lake cells");
        Check(fill.Cuts.Length == 0 && fill.Lakes.Length == 1 && fill.Lakes.Span[0].WaterCells == 2 && fill.Lakes.Span[0].Level == 18f, $"bowl: fill-only did not keep one two-cell lake at 18 m (lakes {fill.Lakes.Length}, level {(fill.Lakes.Length > 0 ? fill.Lakes.Span[0].Level : -1)})");
        // Excavation: the cheapest path 6,7,8,9 is cut to the pit's height 10 and the lake disappears.
        DrainageField breach = Bake(Bowl(18f), 0d);
        Check(breach.Cuts.Length == 1 && breach.Cuts.Span[0].Path.SequenceEqual(new[] { 6, 7, 8, 9 }) && breach.Cuts.Span[0].MaxDepthMeters == 8 && breach.Cuts.Span[0].CostMeters == 10, "bowl: excavation path, depth or cost differs");
        Check(breach.Lakes.Length == 0 && breach.CutRouteHeight[7] == 10 && breach.CutRouteHeight[8] == 10 && breach.RouteHeight[7] == 18f && breach.CutRouteHeight[9] == 0, "bowl: the cut did not lower the sill, kept the lake, or changed the input or the outlet");
        Check(breach.AreaCells[9] == 4 && Math.Abs(breach.RunoffAccumulation[9] - 4.0) < 1e-12 && Math.Abs(breach.Runoff[6] - 1.0) < 1e-12 && breach.Report.Conserved, "bowl: outlet lost area or runoff");
        // T2R-5c: a perched lake beside a low barrier. The barrier row (5 m) is not a spill candidate, so the level is the 90 m sill, not 5 m.
        {
            const int w = 5;
            var h = new float[15];
            new[] { 100f, 70f, 80f, 90f, 0f }.CopyTo(h, w);
            for (int i = 0; i < 5; i++) { h[i] = 5; h[10 + i] = 1000; }
            var kind = Enumerable.Repeat(DrainageCellKind.Barrier, 15).ToArray();
            Array.Fill(kind, DrainageCellKind.Land, w, 4);
            kind[9] = DrainageCellKind.Ocean;
            DrainageField perched = Bake(Make(w, 3, h, kind), 1d);
            Check(perched.Lakes.Length == 1 && perched.Lakes.Span[0].Level == 90f && perched.Lakes.Span[0].WaterCells == 2, $"perched lake beside a low barrier: level {(perched.Lakes.Length > 0 ? perched.Lakes.Span[0].Level : -1)} water {(perched.Lakes.Length > 0 ? perched.Lakes.Span[0].WaterCells : -1)}, expected one two-cell lake at 90 m");
        }
        // Impossible depth: a 50 m sill is over the 40 m limit, so the lake stays.
        DrainageField tooDeep = Bake(Bowl(60f), 0d);
        Check(tooDeep.Cuts.Length == 0 && tooDeep.Lakes.Length == 1 && tooDeep.Report.BreachFeasible == 0, "bowl: a sill over 40 m was cut");
        // Abundance 1 leaves every feasible breach uncut.
        Check(Bake(Bowl(18f), 1d).Cuts.Length == 0, "lake_abundance 1 still cut");
        // An exit at the pit's height is not certified lower ground.
        Check(Bake(Bowl(18f, 10f), 0d).Report.BreachFeasible == 0, "bowl: a breach was accepted into an exit at the pit height");

        // 32-cell limit: pit at column 1, 12 m sill cells to an ocean that is 31 or 37 steps away.
        foreach ((int steps, bool feasible) in new[] { (31, true), (33, false) })
        {
            int w = steps + 3;
            var h = new float[3 * w];
            var kind = Enumerable.Repeat(DrainageCellKind.Barrier, h.Length).ToArray();
            for (int i = 0; i < w; i++) { h[i] = 1000; h[2 * w + i] = 1000; }
            for (int i = 0; i < w; i++) { h[w + i] = i == 0 ? 50 : i == 1 ? 10 : 12; kind[w + i] = DrainageCellKind.Land; }
            h[w + w - 1] = 0; kind[w + w - 1] = DrainageCellKind.Ocean;
            DrainageField run = Bake(Make(w, 3, h, kind), 0d);
            Check((run.Report.BreachFeasible == 1) == feasible, $"{steps} steps from the pit: feasible {run.Report.BreachFeasible}, expected {(feasible ? 1 : 0)}");
        }

        // Seam and blocked diagonals: neighbour tables against an independent oracle, wrapped and unwrapped.
        float[] seamHeight = { 0, 15, 10, 15, 15, 15 };
        DrainageCellKind[] seamKind = { DrainageCellKind.Ocean, DrainageCellKind.Land, DrainageCellKind.Land, DrainageCellKind.Land, DrainageCellKind.Land, DrainageCellKind.Barrier };
        DrainageDomain wrapped = Make(3, 2, seamHeight, seamKind, wrap: true), plain = Make(3, 2, seamHeight, seamKind);
        CheckNeighbours(wrapped, "wrapped seam");
        CheckNeighbours(plain, "unwrapped rim");
        Check(wrapped.TryNeighbor(2, 4, out int across) && across == 0 && wrapped.StepMeters(4) == 1000 && !plain.TryNeighbor(2, 4, out _), "seam: wrap is not exactly one step");
        Check(!wrapped.TryNeighbor(2, 1, out _) && !wrapped.TryNeighbor(2, 7, out _), "across edge or wrapped barrier corner leaked");
        Check(Bake(wrapped, 1d).Down[2] == 0, "seam: drainage missed the ocean across the seam");
        var cornerKind = new[] { DrainageCellKind.Land, DrainageCellKind.Barrier, DrainageCellKind.Barrier, DrainageCellKind.Land };
        DrainageDomain corner = Make(2, 2, new float[] { 5, 9, 9, 3 }, cornerKind);
        Check(!corner.TryNeighbor(0, 7, out _) && !corner.TryNeighbor(3, 0, out _), "blocked diagonals: a link passed two barrier corners");
        var halfBlocked = new[] { DrainageCellKind.Land, DrainageCellKind.Barrier, DrainageCellKind.Land, DrainageCellKind.Ocean };
        Check(!Make(2, 2, new float[] { 5, 9, 9, 0 }, halfBlocked).TryNeighbor(0, 7, out _), "blocked diagonals: a link passed one blocked orthogonal neighbour");

        // Flat crossing repair on a 2 x 2 flat: the crossing diagonals {0->3, 1->2} become 0->2.
        var flatKind = new[] { DrainageCellKind.Land, DrainageCellKind.Land, DrainageCellKind.Ocean, DrainageCellKind.Ocean };
        DrainageDomain flat = Make(2, 2, new float[] { 10, 10, 10, 10 }, flatKind);
        int[] down = { 3, 2, -1, -1 };
        int[] rank = { 2, 3, 0, 1 };
        var terminal = Enumerable.Repeat(DrainageCellKind.Ocean, 4).ToArray();
        (int repaired, int unresolved, _) = DrainageRouting.RepairCrossingsForTest(flat, new float[] { 10, 10, 10, 10 }, down, rank, terminal);
        Check(repaired == 1 && unresolved == 0 && down.SequenceEqual(new[] { 2, 2, -1, -1 }), $"flat crossing was not repaired to 0->2 (repaired {repaired}, down {string.Join(",", down)})");
    }

    private void CheckNeighbours(DrainageDomain d, string label)
    {
        Span<int> bulk = stackalloc int[8];
        for (int cell = 0; cell < d.Count; cell++)
        {
            d.GetNeighbors(cell, bulk);
            for (int dir = 0; dir < 8; dir++)
            {
                int expected = ReferenceNeighbour(d, cell, dir);
                bool found = d.TryNeighbor(cell, dir, out int single);
                if (bulk[dir] != expected || single != expected || found != (expected >= 0)) { Fail($"{label}: neighbour {cell}/{dir} bulk {bulk[dir]} single {single}, expected {expected}"); return; }
            }
        }
    }

    private static int ReferenceNeighbour(DrainageDomain d, int cell, int direction)
    {
        if (d.Kind[cell] == DrainageCellKind.Barrier) return -1;
        int ordinal = direction < 4 ? direction : direction + 1;
        int sourceColumn = cell % d.Width, sourceRow = cell / d.Width;
        int row = sourceRow + ordinal / 3 - 1, column = sourceColumn + ordinal % 3 - 1;
        if (row < 0 || row >= d.Height) return -1;
        if (column < 0 || column >= d.Width) { if (!d.Wrap) return -1; column = (column + d.Width) % d.Width; }
        int index = row * d.Width + column;
        if (d.Kind[index] == DrainageCellKind.Barrier) return -1;
        if (row != sourceRow && ordinal % 3 != 1 && (d.Kind[sourceRow * d.Width + column] == DrainageCellKind.Barrier || d.Kind[row * d.Width + sourceColumn] == DrainageCellKind.Barrier)) return -1;
        return index;
    }

    // ------------------------------------------------------------------ strips

    private void Strips()
    {
        TerrainGenerationParameters recipe = DrainageGridTest.Recipe();
        using RingTerrainSampler root = DrainageGridTest.CreateSampler(recipe);
        DrainageClimateInputs climate = DrainageClimateInputs.FromSampler(root);
        int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
        var source = new CpuDrainageHeightSource(recipe, climate, workers);
        foreach ((string label, DrainageGridSpec spec) in DrainageGridTest.Strips(recipe))
        {
            DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
            DrainageField many = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None);
            DrainageField again = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None);
            DrainageField one = DrainageBake.Bake(grid, recipe, 1, null, CancellationToken.None);
            string hashes = $"{many.Report.FilledHash}/{many.Report.LakeHash}/{many.Report.DownHash}";
            Check(hashes == $"{again.Report.FilledHash}/{again.Report.LakeHash}/{again.Report.DownHash}", $"{label}: two runs gave different hashes");
            Check(hashes == $"{one.Report.FilledHash}/{one.Report.LakeHash}/{one.Report.DownHash}", $"{label}: 1 worker and {workers} workers gave different hashes");
            var counts = Verify(many, out string detail);
            Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: {detail}");
            foreach ((string name, int bad, int total, double fraction) in counts)
                Check(bad == 0 || (fraction > 0 && (double)(total - bad) / total >= 0.995 && name.StartsWith("lake_", StringComparison.Ordinal)), $"{label}: invariant {name} failed on {bad} of {total}");
            Check(many.Report.EnclosedLandShare <= 0.01, $"{label}: enclosed land {many.Report.EnclosedLandShare:P2}");
            Check(many.Report.Conserved, $"{label}: area not conserved");
            Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: report {many.Report.ToText().Replace('\n', ' ')}");

            // Negative controls on copies of the real arrays.
            int[] down = many.Down.ToArray();
            int landCell = Enumerable.Range(0, many.Count).First(i => many.Kind[i] == DrainageCellKind.Land && down[i] >= 0 && many.Kind[down[i]] == DrainageCellKind.Land && down[down[i]] >= 0);
            int[] cyc = (int[])down.Clone();
            cyc[down[landCell]] = landCell;
            Check(Verify(many, cyc, out _).First(c => c.Name == "no_cycles").Bad > 0, $"{label}: negative control: a fabricated cycle was not flagged");
            int[] pit = (int[])down.Clone();
            pit[landCell] = -1;
            Check(Verify(many, pit, out _).First(c => c.Name == "reaches_outlet").Bad > 0, $"{label}: negative control: an undrained pit was not flagged");
            int barrier = Enumerable.Range(0, many.Count).FirstOrDefault(i => many.Kind[i] == DrainageCellKind.Barrier, -1);
            if (barrier >= 0)
            {
                int neighbour = -1;
                for (int dir = 0; dir < 8 && neighbour < 0; dir++) { int n = many.Domain.GeometricNeighbor(barrier, dir); if (n >= 0 && many.Kind[n] == DrainageCellKind.Land) neighbour = n; }
                if (neighbour >= 0)
                {
                    int[] leak = (int[])down.Clone();
                    leak[neighbour] = barrier;
                    Check(Verify(many, leak, out _).First(c => c.Name == "no_barrier_flow").Bad > 0, $"{label}: negative control: a barrier leak was not flagged");
                }
            }
        }
    }

    private (string Name, int Bad, int Total, double Fraction)[] Verify(DrainageField f, out string detail) => Verify(f, f.Down.ToArray(), out detail);

    /// <summary>Structural and lake invariants over every cell, with the down array under test supplied separately so the negative controls can corrupt it.</summary>
    private (string Name, int Bad, int Total, double Fraction)[] Verify(DrainageField f, int[] down, out string detail)
    {
        DrainageDomain d = f.Domain;
        int n = f.Count;
        float[] filled = f.FilledHeight.ToArray();
        // Cycles and outlets by an independent walk with memoisation: 0 unknown, 1 on the current path, 2 reaches an outlet, 3 dead end, 4 cycle.
        var state = new byte[n];
        var stack = new List<int>();
        int cycles = 0, noOutlet = 0, landVisited = 0;
        for (int start = 0; start < n; start++)
        {
            if (!d.IsLand(start) || !f.Visited[start] || d.IsOutlet(start) || state[start] != 0) continue;
            stack.Clear();
            int cell = start;
            byte result;
            while (true)
            {
                if (d.IsOutlet(cell)) { result = 2; break; }
                if (state[cell] == 1) { result = 4; break; }
                if (state[cell] != 0) { result = state[cell]; break; }
                state[cell] = 1;
                stack.Add(cell);
                if (down[cell] < 0) { result = 3; break; }
                cell = down[cell];
            }
            foreach (int s in stack) state[s] = result;
        }
        for (int i = 0; i < n; i++)
        {
            if (!d.IsLand(i) || !f.Visited[i] || d.IsOutlet(i)) continue;
            landVisited++;
            if (state[i] == 4) cycles++;
            else if (state[i] != 2) noOutlet++;
        }
        int rises = 0, barrierFlow = 0, links = 0;
        for (int i = 0; i < n; i++)
        {
            int next = down[i];
            if (next < 0 || !f.Visited[i]) continue;
            links++;
            if (filled[next] > filled[i]) rises++;
            if (d.Kind[i] == DrainageCellKind.Barrier || d.Kind[next] == DrainageCellKind.Barrier || d.Direction(i, next) < 0) barrierFlow++;
        }
        int cutBad = 0;
        foreach (DrainageBreach cut in f.Cuts.Span)
            if (cut.MaxDepthMeters > DrainageConditioning.MaxBreachDepthMeters + 1e-3 || cut.Path.Length - 1 > DrainageConditioning.MaxBreachSteps) cutBad++;
        // Lake containment: every neighbour water can flow to (not a barrier, no blocked diagonal; T2R-5c) of lake water is the same lake's water or has its lowest sample at or above the level; no lake water touches lower water.
        int water = 0, dryBad = 0, lowerBad = 0;
        ReadOnlySpan<DrainageLake> lakes = f.Lakes.Span;
        var examples = new List<string>();
        for (int i = 0; i < n; i++)
        {
            if (!f.IsLakeWater(i)) continue;
            water++;
            DrainageLake lake = lakes[f.LakeId[i]];
            for (int dir = 0; dir < 8; dir++)
            {
                if (!d.TryNeighbor(i, dir, out int m)) continue;
                if (f.IsLakeWater(m) && f.LakeId[m] == lake.Id) continue;
                if (f.Lowest[m] >= lake.Level) continue;
                if (f.IsLakeWater(m) || d.IsOutlet(m)) lowerBad++; else dryBad++;
                if (examples.Count < 4) examples.Add($"cell {i}->{m} level {lake.Level} lowest {f.Lowest[m]} kind {d.Kind[m]}");
                break;
            }
        }
        detail = $"cells={n} land_visited={landVisited} links={links} lakes={lakes.Length} water_cells={water} cuts={f.Cuts.Length} enclosed={f.Report.EnclosedLandCells} cycles={cycles} no_outlet={noOutlet} rises={rises} barrier_flow={barrierFlow} cut_bad={cutBad} lake_dry_bad={dryBad} lake_lower_bad={lowerBad}" + (examples.Count > 0 ? " examples: " + string.Join("; ", examples) : "");
        return new[]
        {
            ("no_cycles", cycles, landVisited, 0d),
            ("reaches_outlet", noOutlet, landVisited, 0d),
            ("filled_never_rises", rises, links, 0d),
            ("no_barrier_flow", barrierFlow, links, 0d),
            ("cuts_within_limits", cutBad, Math.Max(1, f.Cuts.Length), 0d),
            ("lake_dry_neighbour_at_or_above_level", dryBad, Math.Max(1, water), 1d),
            ("lake_no_touch_lower_water", lowerBad, Math.Max(1, water), 1d),
        };
    }

    // ------------------------------------------------------------------ rivers (T2R-5)

    private static int LevelRises(RiverNetwork net, int reach)
    {
        RiverReach r = net.Reaches[reach];
        int rises = 0;
        double previous = RiverLevel.At(net, reach, r.SStart);
        for (double s = r.SStart + 10; s <= r.SEnd; s += 10)
        {
            double level = RiverLevel.At(net, reach, s);
            if (level > previous + 1e-6) rises++;
            previous = level;
        }
        return rises;
    }

    // ------------------------------------------------------------------ graph, index and file (T2R-6)

    private static int IndexViolations(RiverNetwork net, RiverSpatialIndex index, RiverGraph graph, (double Around, double Across)[] points, double[] boxCentreAround, double[] boxCentreAcross, double[] boxHalfAround, double[] boxHalfAcross, out int checkedPieces)
    {
        int violations = 0;
        checkedPieces = 0;
        const int steps = 32;
        foreach ((double around, double across) in points)
        {
            ReadOnlySpan<int> listed = index.PiecesNear(around, across);
            bool near = index.IsNear(around, across);
            for (int p = 0; p < boxCentreAround.Length; p++)
            {
                if (Math.Abs(net.WrapDelta(around - boxCentreAround[p])) > boxHalfAround[p] || Math.Abs(across - boxCentreAcross[p]) > boxHalfAcross[p]) continue;
                int stem = graph.PieceStem[p], k = p - net.Stems[stem].NodeStart;
                RiverStemCurve c = net.Curves[stem];
                double best = double.PositiveInfinity;
                for (int i = 0; i <= steps; i++)
                {
                    c.EvalPiece(k, (double)i / steps, out double x, out double y, out _, out _);
                    double da = net.WrapDelta(around - (c.OriginAround + x)), dc = across - (c.OriginAcross + y);
                    best = Math.Min(best, Math.Sqrt(da * da + dc * dc));
                }
                if (best > RiverSpatialIndex.ReachRadiusMeters - 1) continue;
                checkedPieces++;
                if (!near || listed.IndexOf(p) < 0) violations++;
            }
        }
        return violations;
    }

    private void GraphChecks(string label, RiverNetwork net, RiverNetwork again, DrainageField field, DrainageGrid grid, DrainageGridSpec spec, double thresholdKm2)
    {
        RiverGraph graph = RiverGraph.Build(net, field, grid, 0x1234UL, 0);
        RiverGraph graph2 = RiverGraph.Build(again, field, grid, 0x1234UL, 1);
        Check(graph.IdentityHash == graph2.IdentityHash, $"{label}: the graph identity hash differs between two bakes (or depends on the source byte)");

        // File round trip: byte-equal, same hash, same derived values.
        byte[] bytes = RiverGraphFormat.Write(graph);
        Check(RiverGraphFormat.TryRead(bytes, 0x1234UL, out RiverGraph? loaded, out string error) && loaded != null, $"{label}: the graph file did not read back: {error}");
        if (loaded != null)
        {
            Check(RiverGraphFormat.Write(loaded).AsSpan().SequenceEqual(bytes), $"{label}: serialise, deserialise, serialise is not byte-equal");
            Check(loaded.IdentityHash == graph.IdentityHash, $"{label}: the loaded graph has another identity hash");
            RiverNetwork ln = loaded.Network;
            bool tailsEqual = true;
            for (int i = 0; i < net.Stems.Length; i++)
                tailsEqual &= BitConverter.DoubleToInt64Bits(ln.ReceivingLevel[i]) == BitConverter.DoubleToInt64Bits(net.ReceivingLevel[i]) && ln.TailMeters[i] == net.TailMeters[i] && ln.TailDrop[i] == net.TailDrop[i]
                    && ln.Stems[i].NodeStart == net.Stems[i].NodeStart && ln.Stems[i].KnotStart == net.Stems[i].KnotStart && ln.Stems[i].ReachStart == net.Stems[i].ReachStart;
            Check(tailsEqual, $"{label}: the tail levels or stem starts derived at load differ from the baked ones");
            var rng0 = new Random(77);
            int levelBad = 0, indexBad = 0;
            for (int i = 0; i < 2000 && net.Stems.Length > 0; i++)
            {
                int stem = rng0.Next(net.Stems.Length);
                double s = rng0.NextDouble() * net.Curves[stem].Length;
                if (BitConverter.DoubleToInt64Bits(RiverLevel.AtStem(ln, stem, s)) != BitConverter.DoubleToInt64Bits(RiverLevel.AtStem(net, stem, s))) levelBad++;
                net.Centre(stem, s, out double a, out double c, out _, out _);
                a = net.WrapAround(a);
                if (!graph.ReachesNear(a, c).SequenceEqual(loaded.ReachesNear(a, c)) || graph.IsNear(a, c) != loaded.IsNear(a, c)) indexBad++;
            }
            Check(levelBad == 0 && indexBad == 0, $"{label}: loaded graph differs from the baked one (levels {levelBad}, index {indexBad} of 2000)");
        }
        // Negative controls: a flipped payload byte, a wrong header hash, a wrong input identity, a short file and a bad magic must each be refused.
        byte[] flipped = (byte[])bytes.Clone();
        flipped[RiverGraphFormat.HeaderBytes + (flipped.Length - RiverGraphFormat.HeaderBytes) / 2] ^= 0x10;
        Check(!RiverGraphFormat.TryRead(flipped, null, out _, out _), $"{label}: negative control: a corrupted payload byte was accepted");
        byte[] badHash = (byte[])bytes.Clone();
        badHash[29] ^= 1;
        Check(!RiverGraphFormat.TryRead(badHash, null, out _, out _), $"{label}: negative control: a corrupted hash was accepted");
        Check(!RiverGraphFormat.TryRead(bytes, 0x1235UL, out _, out _), $"{label}: negative control: a different input identity was accepted");
        Check(!RiverGraphFormat.TryRead(bytes.AsSpan(0, bytes.Length - 5).ToArray(), null, out _, out _), $"{label}: negative control: a truncated file was accepted");
        byte[] badMagic = (byte[])bytes.Clone();
        badMagic[0] = (byte)'X';
        Check(!RiverGraphFormat.TryRead(badMagic, null, out _, out _), $"{label}: negative control: a bad magic was accepted");

        // The index against brute force at 10,000 points: every piece within 650 m is listed and Near is set.
        int pieces = net.NodeAroundDm.Length;
        var centreAround = new double[pieces];
        var centreAcross = new double[pieces];
        var halfAround = new double[pieces];
        var halfAcross = new double[pieces];
        for (int stem = 0; stem < net.Stems.Length; stem++)
        {
            RiverStemCurve c = net.Curves[stem];
            for (int k = 0; k < net.Stems[stem].NodeCount; k++)
            {
                c.Controls(k, out double ax, out double ay, out double px, out double py, out double cx, out double cy);
                int p = net.Stems[stem].NodeStart + k;
                double x0 = Math.Min(ax, Math.Min(px, cx)), x1 = Math.Max(ax, Math.Max(px, cx)), y0 = Math.Min(ay, Math.Min(py, cy)), y1 = Math.Max(ay, Math.Max(py, cy));
                centreAround[p] = c.OriginAround + 0.5 * (x0 + x1); centreAcross[p] = c.OriginAcross + 0.5 * (y0 + y1);
                halfAround[p] = 0.5 * (x1 - x0) + RiverSpatialIndex.ReachRadiusMeters; halfAcross[p] = 0.5 * (y1 - y0) + RiverSpatialIndex.ReachRadiusMeters;
            }
        }
        var rng = new Random(4242);
        var points = new (double, double)[10_000];
        double stripStart = spec.ColumnStart * spec.AroundStepMeters, stripLength = spec.Columns * spec.AroundStepMeters;
        for (int i = 0; i < points.Length; i++)
        {
            if (i % 2 == 0 || pieces == 0) points[i] = (net.WrapAround(stripStart + rng.NextDouble() * stripLength), rng.NextDouble() * net.WidthM);
            else
            {
                int p = rng.Next(pieces);
                points[i] = (net.WrapAround(centreAround[p] + (rng.NextDouble() * 2 - 1) * 1500), Math.Clamp(centreAcross[p] + (rng.NextDouble() * 2 - 1) * 1500, 0, net.WidthM - 1e-3));
            }
        }
        int violations = IndexViolations(net, graph.Index, graph, points, centreAround, centreAcross, halfAround, halfAcross, out int checkedPieces);
        Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: index vs brute force: {points.Length} points, {checkedPieces} (point, piece) pairs within 650 m, {violations} missing; pieces {pieces}, bucket entries {graph.Index.EntryCount}");
        Check(violations == 0 && checkedPieces > 1000, $"{label}: the index misses {violations} of {checkedPieces} pieces within 650 m");
        if (pieces > 0)
        {
            int stem = net.Stems.Length / 2;
            int piece = net.Stems[stem].NodeStart + net.Stems[stem].NodeCount / 2;
            RiverStemCurve c = net.Curves[stem];
            c.EvalPiece(net.Stems[stem].NodeCount / 2, 0.5, out double x, out double y, out _, out _);
            double pa = net.WrapAround(c.OriginAround + x), pc = c.OriginAcross + y;
            RiverSpatialIndex missing = graph.Index.WithoutEntry(pa, pc, piece);
            int control = IndexViolations(net, missing, graph, new[] { (pa, pc) }, centreAround, centreAcross, halfAround, halfAcross, out _);
            Check(control > 0, $"{label}: negative control: an index bucket missing a segment was not flagged");
        }

        // Lakes: one outlet at most and the same stem named by the lake, inlets on or next to the lake's cells, cells against the field, LakeAt and the bitsets against the runs.
        int[] outletCount = new int[graph.Lakes.Length];
        for (int i = 0; i < net.Stems.Length; i++)
        {
            int up = net.Reaches[net.Stems[i].ReachStart].UpstreamLake;
            if (up >= 0 && net.Stems[i].Terminal != RiverTerminal.Dead) outletCount[up]++;
        }
        int noOutlet = 0, multiOutlet = 0, outletMismatch = 0, inletBad = 0, inlets = 0, cellBad = 0;
        long lakeCells = 0;
        for (int l = 0; l < graph.Lakes.Length; l++)
        {
            RiverLake lake = graph.Lakes[l];
            if (outletCount[l] == 0) noOutlet++;
            if (outletCount[l] > 1) multiOutlet++;
            if (outletCount[l] == 0 ? lake.OutletStem != -1 : net.Reaches[net.Stems[lake.OutletStem].ReachStart].UpstreamLake != l) outletMismatch++;
            if (lake.CellCount != field.Lakes.Span[l].FloodedCells || lake.Level != Math.Floor(field.Lakes.Span[l].Level)) cellBad++;
            lakeCells += lake.CellCount;
            for (int i = lake.InletStart; i < lake.InletStart + lake.InletCount; i++)
            {
                RiverInlet inlet = graph.Inlets[i];
                inlets++;
                int col = graph.Index.ColumnOf(inlet.AroundDm / 10d), row = graph.Index.RowOf(inlet.AcrossDm / 10d);
                bool onOrNext = false;
                for (int dy = -1; dy <= 1 && !onOrNext; dy++)
                    for (int dx = -1; dx <= 1 && !onOrNext; dx++)
                        onOrNext = graph.LakeAt((col + dx + graph.RingColumns) % graph.RingColumns, row + dy) == l;
                if (!onOrNext || net.Stems[inlet.Stem].Terminal != RiverTerminal.Lake || net.Stems[inlet.Stem].TerminalId != l) inletBad++;
            }
        }
        // A lake without an outlet river spills into another lake, an outlet (sea, Weather Lung, strip end), a cell below the channel threshold, or (reported, not asserted) the middle of an existing channel.
        int spillCascade = 0, spillOutlet = 0, spillSmall = 0, spillOther = 0, noSpill = 0;
        var spill = new int[graph.Lakes.Length];
        Array.Fill(spill, -1);
        ReadOnlySpan<int> down = field.Down;
        for (int c = 0; c < field.Count; c++)
        {
            int id = field.LakeId[c];
            if (id >= 0 && down[c] >= 0 && field.LakeId[down[c]] != id && spill[id] < 0) spill[id] = down[c];
        }
        for (int l = 0; l < graph.Lakes.Length; l++)
        {
            if (graph.Lakes[l].OutletStem >= 0) continue;
            int t = spill[l];
            if (t < 0) noSpill++;
            else if (field.LakeId[t] >= 0) spillCascade++;
            else if (field.Domain.IsOutlet(t)) spillOutlet++;
            else if (field.AreaCells[t] * field.CellAreaKm2 < thresholdKm2 - 1e-9 || field.Kind[t] == DrainageCellKind.Barrier) spillSmall++;
            else spillOther++;
        }
        Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: lakes {graph.Lakes.Length}, without an outlet river {noOutlet} (spill into another lake {spillCascade}, into an outlet {spillOutlet}, into a cell under the threshold {spillSmall}, no spill cell {noSpill}, into a channel cell with no stem head of its own {spillOther}), with several {multiOutlet}, inlets {inlets}, flooded cells {lakeCells}, runs {graph.Runs.Length}");
        Check(multiOutlet == 0 && outletMismatch == 0, $"{label}: {multiOutlet} lakes have several outlets, {outletMismatch} outlet records differ from the reaches");
        Check(inletBad == 0, $"{label}: {inletBad} of {inlets} inlets are not on or next to their lake's cells");
        Check(cellBad == 0, $"{label}: {cellBad} lake records differ from the conditioned field (cell count or level)");
        int laBad = 0;
        var rng2 = new Random(99);
        for (int i = 0; i < 20_000; i++)
        {
            int col = rng2.Next(field.Width), row = rng2.Next(field.Height);
            int expected = field.LakeId[row * field.Width + col];
            int ringCol = spec.RingColumn(col);
            if (graph.LakeAt(ringCol, row) != expected || graph.Index.IsFlooded(ringCol, row) != (expected >= 0) || (expected >= 0 && !graph.Index.IsNear(ringCol, row))) laBad++;
        }
        for (int l = 0; l < graph.Lakes.Length && l < 400; l++)
        {
            RiverLake lake = graph.Lakes[l];
            if (graph.LakeAt(lake.DeepestCol, lake.DeepestRow) != l) laBad++;
        }
        Check(laBad == 0, $"{label}: LakeAt or the flooded bitset disagrees with the field on {laBad} cells");
        Check(graph.ResidentBytes < 32L * 1024 * 1024, $"{label}: graph resident estimate {graph.ResidentBytes} is above 32 MB");
        Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: file {bytes.Length} bytes, resident {graph.ResidentBytes} bytes, hash {graph.IdentityHash:X16}");
    }

    private static string DrainageFixturePath =>ProjectSettings.GlobalizePath("res://tools/fixtures/hydrology/d4_baseline/graph.json.gz");

    private void Rivers()
    {
        TerrainGenerationParameters recipe = DrainageGridTest.Recipe();
        using RingTerrainSampler root = DrainageGridTest.CreateSampler(recipe);
        DrainageClimateInputs climate = DrainageClimateInputs.FromSampler(root);
        int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
        var source = new CpuDrainageHeightSource(recipe, climate, workers);
        foreach ((string label, DrainageGridSpec spec) in DrainageGridTest.Strips(recipe))
        {
            DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
            DrainageField field = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None);
            var options = new RiverBuildOptions { Source = source, Workers = workers, KeepStations = true };
            RiverNetwork net = RiverGraphBuilder.Build(field, grid, recipe, options, CancellationToken.None, out RiverBuildReport report);
            RiverNetwork again = RiverGraphBuilder.Build(field, grid, recipe, options, CancellationToken.None, out _);
            Check(net.NodeAroundDm.AsSpan().SequenceEqual(again.NodeAroundDm) && net.Knots.Count == again.Knots.Count, $"{label}: two river builds differ");
            GraphChecks(label, net, again, field, grid, spec, report.ThresholdKm2);
            Console.WriteLine($"DRAINAGE_GRAPH_DETAIL {label}: " + report.ToText().Replace('\n', ' '));
            Check(report.TerminalDead == 0 && report.Stems > 0, $"{label}: {report.TerminalDead} stems end nowhere, {report.Stems} stems");
            foreach (RiverStem st in net.Stems)
            {
                RiverStem cur = st;
                int hops = 0;
                while (cur.Terminal == RiverTerminal.Confluence && hops++ < 100000) cur = net.Stems[cur.TerminalId];
                Check(cur.Terminal is RiverTerminal.Sea or RiverTerminal.Lake or RiverTerminal.Lung or RiverTerminal.StripEnd, $"{label}: a stem ends in {cur.Terminal}");
            }
            // The level never rises downstream: 200 reaches every 10 m.
            int[] longReaches = Enumerable.Range(0, net.Reaches.Length).Where(r => net.Reaches[r].SEnd - net.Reaches[r].SStart >= 100f).ToArray();
            int stride = Math.Max(1, longReaches.Length / 200), sampled = 0, rising = 0;
            for (int i = 0; i < longReaches.Length && sampled < 200; i += stride, sampled++) rising += LevelRises(net, longReaches[i]);
            Check(rising == 0, $"{label}: the level rises downstream {rising} times on {sampled} reaches");
            if (sampled > 0)
            {
                // Negative control: a profile that jumps up must be flagged.
                int stemIndex = net.Reaches[longReaches[0]].Stem;
                RiverStem st = net.Stems[stemIndex];
                RiverKnotTable saved = net.Knots;
                var raisedLists = Enumerable.Range(0, net.Stems.Length).Select(i => saved.StemCm(i)).ToArray();
                List<(int S, int W)> raisedStem = raisedLists[stemIndex];
                for (int k = raisedStem.Count / 2; k < raisedStem.Count; k++) raisedStem[k] = (raisedStem[k].S, raisedStem[k].W + 800);
                net.Knots = RiverKnotTable.FromCm(raisedLists);
                int flagged = 0;
                foreach (int r in longReaches.Where(r => net.Reaches[r].Stem == stemIndex)) flagged += LevelRises(net, r);
                net.Knots = saved;
                Check(st.KnotCount <= 2 || flagged > 0, $"{label}: negative control: a rising profile was not flagged");
            }
            // Stored profile against the sampled envelope at every station (fills and holds excepted).
            int envBad = 0, envTotal = 0;
            for (int i = 0; i < net.Stems.Length; i++)
            {
                RiverStemStations st = net.Stations![i];
                for (int j = 0; j < st.S.Length; j++)
                {
                    double stored = RiverLevel.Stored(net, i, st.S[j]);
                    envTotal++;
                    bool ok = stored <= st.Profile[j] + 1e-3 && stored >= st.Profile[j] - RiverLevel.KnotBand - 1e-3 && (st.Filled[j] || stored <= st.Running[j] + 1e-3);
                    if (!ok && ++envBad <= 3) Fail($"{label}: stem {i} station {j} s={st.S[j]} stored {stored:F3} profile {st.Profile[j]:F3} running {st.Running[j]:F3}");
                }
            }
            Check(envBad == 0, $"{label}: {envBad} of {envTotal} stations outside the knot band");
            // Discharge conserved at every cell with incoming flow.
            var inflow = new double[field.Count];
            int[] down = field.Down.ToArray();
            for (int c = 0; c < field.Count; c++) if (down[c] >= 0 && field.Kind[c] == DrainageCellKind.Land) inflow[down[c]] += field.RunoffAccumulation[c];
            int conservationBad = 0;
            for (int c = 0; c < field.Count; c++)
                if (field.Kind[c] == DrainageCellKind.Land && field.Visited[c] && inflow[c] > 0 && Math.Abs(field.RunoffAccumulation[c] - (inflow[c] + field.Runoff[c])) > 1e-6 * field.RunoffAccumulation[c]) conservationBad++;
            Check(conservationBad == 0, $"{label}: discharge is not conserved at {conservationBad} cells");
            // Hydraulic relations.
            int relBad = 0;
            for (int r = 0; r < net.Reaches.Length; r++)
            {
                RiverSection sec = net.Sections[r];
                (double manning, double cap) = RiverHydraulics.SpeedLimits(sec.Depth, sec.Gradient, net.Ferocity);
                if (Math.Abs(sec.Q - sec.Speed * sec.Width * sec.Depth) > 1e-6 * sec.Q || sec.Depth < 1.2 - 1e-9 || sec.Width < 8 - 1e-9 || sec.Speed > Math.Min(manning, cap) * (1 + 1e-9)) relBad++;
            }
            Check(relBad == 0, $"{label}: {relBad} reaches break Q = U w d, d >= 1.2, w >= 8 or the speed limits");
            for (double g = 1e-4; g < 0.1; g *= 3)
            {
                double last = 0;
                for (double q = 1; q < 1e5; q *= 1.7) { double dep = RiverHydraulics.Section(q, g, 1, 1).Depth; Check(dep >= last - 1e-12, $"depth decreases with Q at S {g}"); last = dep; }
            }
            // Lateral displacement within the cap, and no centreline point inside a barrier cell or a site's corridor.
            Check(report.MaxNodeShiftMeters <= RiverWander.NodeShiftCapMeters + 1e-6 && report.MaxSmoothMeters <= RiverWander.NodeShiftCapMeters + 1e-6, $"{label}: node shift beyond the 350 m cap (total {report.MaxNodeShiftMeters:F1}, smoothing {report.MaxSmoothMeters:F1})");
            Check(report.MaxWanderMeters <= RiverHydraulics.WanderAmplitudeMeters + 1e-6 && report.MaxMeanderMeters <= RiverHydraulics.MeanderAmplitudeCapMeters + 1e-6, $"{label}: lateral cap exceeded (wander {report.MaxWanderMeters:F1}, meander {report.MaxMeanderMeters:F1})");
            int barrierBad = 0, meanderBad = 0, vertices = 0;
            var sites = recipe.HasEmitterSites ? EmitterSiteTerrainShaper.GetOrCreate(recipe).Registry.Sites : System.Array.Empty<EmitterSite>();
            double ring = spec.CircumferenceMeters;
            for (int i = 0; i < net.Stems.Length; i++)
                for (double s = 0; s < net.Curves[i].Length; s += 100)
                {
                    net.Centre(i, s, out double around, out double across, out _, out _);
                    double wrapped = net.WrapAround(around);
                    int col = (int)Math.Floor(wrapped / spec.AroundStepMeters) - spec.ColumnStart, row = (int)Math.Floor(across / spec.AcrossStepMeters);
                    if (col < 0) col += spec.RingColumns;
                    vertices++;
                    if (col >= 0 && col < field.Width && row >= 0 && row < field.Height && field.Kind[row * field.Width + col] == DrainageCellKind.Barrier) barrierBad++;
                    foreach (EmitterSite site in sites)
                    {
                        double da = Math.Abs(wrapped - site.Around); da = Math.Min(da, ring - da);
                        double dw = across - site.Across;
                        if (Math.Sqrt(da * da + dw * dw) < site.InfluenceRadius + DrainageBarriers.CorridorRadiusMeters - 1) { barrierBad++; break; }
                    }
                    if (Math.Abs(net.Meander(i, s)) > RiverHydraulics.MeanderAmplitudeCapMeters + 1e-6) meanderBad++;
                }
            Check(barrierBad == 0 && meanderBad == 0, $"{label}: {barrierBad} of {vertices} centreline points inside a barrier clearance, {meanderBad} beyond the meander cap");
        }

        // D4 on the production centrelines: the evaluator first on the frozen baseline, then the 2,000 km strip at 50 km^2.
        PathSegment[] fixture = RiverPathMetrics.ReadFixtureSegments(DrainageFixturePath, out PathGrid maskGrid);
        PathMetrics baseline = RiverPathMetrics.Evaluate(fixture, maskGrid);
        Console.WriteLine("DRAINAGE_GRAPH_DETAIL D4 baseline (re-scored fixture): " + baseline.ToText());
        Check(Math.Abs(baseline.Overall.Share - 0.2072) <= 0.001 && Math.Abs(baseline.Coastal.Share - 0.3715) <= 0.001, $"evaluator does not reproduce the fixture: {baseline.Overall.Share:P3} / {baseline.Coastal.Share:P3}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrainageGridSpec big = DrainageGridSpec.Strip(recipe, 9_804_000d, 2000);
        DrainageGrid bigGrid = source.SampleGrid(big, null, CancellationToken.None);
        double gridSeconds = clock.Elapsed.TotalSeconds;
        DrainageField bigField = DrainageBake.Bake(bigGrid, recipe, workers, null, CancellationToken.None);
        clock.Restart();
        RiverNetwork bigNet = RiverGraphBuilder.Build(bigField, bigGrid, recipe, new RiverBuildOptions { ThresholdKm2 = 50d, Levels = false, Source = source, Workers = workers }, CancellationToken.None, out RiverBuildReport bigReport);
        double buildSeconds = clock.Elapsed.TotalSeconds;
        clock.Restart();
        PathMetrics production = RiverPathMetrics.Evaluate(RiverPathMetrics.FromNetwork(bigNet, big.ColumnStart * big.AroundStepMeters), maskGrid);
        Console.WriteLine($"DRAINAGE_GRAPH_DETAIL D4 strip grid {gridSeconds:F1} s, build {buildSeconds:F1} s, metrics {clock.Elapsed.TotalSeconds:F1} s; {bigReport.ToText().Replace('\n', ' ')}");
        Console.WriteLine("DRAINAGE_GRAPH_DETAIL D4 production: " + production.ToText());
        Check(production.Overall.Share <= baseline.Overall.Share / 2, $"D4 overall share {production.Overall.Share:P3} is above half of {baseline.Overall.Share:P3}");
        Check(production.Coastal.Share <= baseline.Coastal.Share / 2, $"D4 coastal share {production.Coastal.Share:P3} is above half of {baseline.Coastal.Share:P3}");
        Check(production.Overall.FlaggedKm < baseline.Overall.FlaggedKm && production.Coastal.FlaggedKm < baseline.Coastal.FlaggedKm, "D4 flagged kilometres did not fall");
        // Bend counts are reported, not asserted: the node rule (lowest sample) alone raises sharp bends above the baseline's (see the T2R-5 report).
        Console.WriteLine($"DRAINAGE_GRAPH_DETAIL D4 bends (production vs baseline): sharp overall {production.SharpOverall.Share:P4} vs {baseline.SharpOverall.Share:P4}, coastal {production.SharpCoastal.Share:P4} vs {baseline.SharpCoastal.Share:P4}; alternating overall {production.AlternatingOverall.Share:P4} vs {baseline.AlternatingOverall.Share:P4}, coastal {production.AlternatingCoastal.Share:P4} vs {baseline.AlternatingCoastal.Share:P4}");
        // Negative control: a straight 7 km polyline must be flagged.
        var straight = new PathPolyline(Enumerable.Range(0, 281).Select(i => new PathPoint(1000 + i * 25d, 5000)).ToArray(), -1, 0);
        PathMetrics line = RiverPathMetrics.Evaluate(RiverPathMetrics.FromPolylines(new[] { straight }), new PathGrid(2000, 318, 1000, 1000, new byte[(2000 * 318 + 7) / 8]));
        Check(line.Overall.Share > 0.8, $"negative control: a straight run was not flagged ({line.Overall.Share:P1})");
    }
}
