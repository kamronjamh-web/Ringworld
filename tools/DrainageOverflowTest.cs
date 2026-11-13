using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Hydrology;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-22 Part A (docs/work-orders/terrain-vegetation/T2-rules.md, RS1): overflows of closed basins on synthetic one-row domains, each with a hand-computed answer. Fixtures use E0 = 10, so a cell of moisture 0.5
/// has runoff 1 and evaporation 5, and a cell of moisture 0.9375 has runoff 1.875 and evaporation 0.625. Cases: two pools (the first full, the saddle cell's own descent pointing into it), a chain of three pools,
/// two pools that fill and merge, a full pool with under 1 m between its floor and its saddle (no lake, the river runs through), and the bit-equality of lake levels, wet cells, verdicts and the mass balance with the
/// overflow links off (<see cref="DrainageLakeBalance.Overflows"/>). A negative control shows the pan case fails with the links off. Prints DRAINAGE_OVERFLOW=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests drainage_overflow_test.
/// </summary>
public partial class DrainageOverflowTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static bool Close(double a, double b, double relative = 1e-9) => Math.Abs(a - b) <= relative * Math.Max(1d, Math.Max(Math.Abs(a), Math.Abs(b)));

    public int Run()
    {
        try
        {
            TwoPools();
            Chain();
            Merge();
            Pan();
            UpstreamOfSill();
            ThreePools();
            Controls();
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_OVERFLOW=FAIL exception " + e); DrainageLakeBalance.Overflows = true; return 1; }
        DrainageLakeBalance.Overflows = true;
        if (_failures.Count != 0) { Console.Error.WriteLine("DRAINAGE_OVERFLOW=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("DRAINAGE_OVERFLOW=PASS");
        return 0;
    }

    /// <summary>
    /// One land row (row 1) between barrier rows; the last cell is the sea. <paramref name="moisture"/> overrides the default 0.5 per column of the land row. <paramref name="below"/> adds land cells in row 2 at the given
    /// columns and heights (the rest of row 2 stays barrier, so a diagonal link out of such a cell is blocked and its only neighbour is the cell above it).
    /// </summary>
    private static DrainageDomain Row(float[] heights, Dictionary<int, float>? moisture = null, Dictionary<int, float>? below = null)
    {
        int w = heights.Length;
        var h = new float[4 * w];
        var kind = Enumerable.Repeat(DrainageCellKind.Barrier, h.Length).ToArray();
        var m = Enumerable.Repeat(0.5f, h.Length).ToArray();
        for (int i = 0; i < w; i++)
        {
            h[i] = 1000; h[2 * w + i] = 1000; h[3 * w + i] = 1000; h[w + i] = heights[i];
            kind[w + i] = i == w - 1 ? DrainageCellKind.Ocean : DrainageCellKind.Land;
            if (moisture != null && moisture.TryGetValue(i, out float v)) m[w + i] = v;
            if (below != null && below.TryGetValue(i, out float b)) { h[2 * w + i] = b; kind[2 * w + i] = DrainageCellKind.Land; }
        }
        return new DrainageDomain(w, 4, false, 1000d, 1000d, kind, h, h, m);
    }

    private static DrainageField Bake(DrainageDomain d, bool overflows)
    {
        DrainageLakeBalance.Overflows = overflows;
        try { return DrainageBake.BakeDomain(null, d, 0.5d, 1, null, CancellationToken.None, 0, 0, 0, 0d, 10d / DrainageLakeBalance.E0); }
        finally { DrainageLakeBalance.Overflows = true; }
    }

    private static bool Cycle(DrainageField f)
    {
        for (int start = 0; start < f.Count; start++)
        {
            int cell = start, steps = 0;
            while (cell >= 0 && steps <= f.Count) { cell = f.Down[cell]; steps++; }
            if (steps > f.Count) return true;
        }
        return false;
    }

    private static int Follow(DrainageField f, int cell, Func<int, bool> stop)
    {
        for (int steps = 0; cell >= 0 && steps <= f.Count; steps++, cell = f.Down[cell]) if (stop(cell)) return cell;
        return -1;
    }

    /// <summary>Lake levels, wet cells, basin verdicts, hashes and the mass balance of two bakes of one domain (overflow links on and off).</summary>
    private void SameArithmetic(DrainageDomain d, string label)
    {
        DrainageField on = Bake(d, true), off = Bake(d, false);
        bool lakes = on.Lakes.Length == off.Lakes.Length;
        if (lakes)
            for (int i = 0; i < on.Lakes.Length; i++)
            {
                DrainageLake a = on.Lakes.Span[i], b = off.Lakes.Span[i];
                if (a.Level != b.Level || a.WaterCells != b.WaterCells || a.FloodedCells != b.FloodedCells || a.PitCell != b.PitCell || a.Inflow != b.Inflow || a.Evaporation != b.Evaporation || a.Closed != b.Closed) lakes = false;
            }
        Check(lakes && on.Report.LakeHash == off.Report.LakeHash && on.Report.FilledHash == off.Report.FilledHash, $"{label}: lake levels, wet cells or inflow differ with the overflow links on and off");
        bool basins = on.Basins.Length == off.Basins.Length;
        if (basins)
            for (int i = 0; i < on.Basins.Length; i++)
            {
                DrainageBasinInfo a = on.Basins.Span[i], b = off.Basins.Span[i];
                if (a.Closed != b.Closed || a.Lakes != b.Lakes || a.DryPans != b.DryPans || a.DryCells != b.DryCells || a.Inflow != b.Inflow || a.Evaporation != b.Evaporation || a.Outflow != b.Outflow || a.FloorLevel != b.FloorLevel) basins = false;
            }
        Check(basins, $"{label}: basin verdicts or dry floors differ with the overflow links on and off");
        Check(on.Report.TotalRunoff == off.Report.TotalRunoff && on.Report.OutflowRunoff == off.Report.OutflowRunoff && on.Report.EvaporatedRunoff == off.Report.EvaporatedRunoff && on.Report.MassError == off.Report.MassError && on.Report.Conserved && off.Report.Conserved,
            $"{label}: the mass balance differs with the overflow links on and off");
        Check(!Cycle(on) && on.Routed.OverflowCycles == 0 && on.Routed.OverflowUnreached == 0, $"{label}: cycle in down, a basin left as it was, or a pool cell the walk missed");
    }

    // ------------------------------------------------------------------ two pools

    /// <summary>
    /// Row: slope 80 70 60 55 (cols 0-3), rim 50 (4), A 10 (5), saddle 20 (6), B 14 (7), rim 50 (8), 40 (9), sea (10). Runoff 1 and evaporation 5 per cell. A catches the slope, the rim and the saddle cell (its descent points
    /// into A: 4 + 1 + 1 + 1 = 7 against a capacity of 5), so A is full and passes 2 over the saddle; B holds its own cell (1; the rim at col 8 drains to the sea past the basin) plus 2 = 3 against 5, so its level is 14 + 3/5 * 6 = 17.6, rounded to 18.
    /// </summary>
    private void TwoPools()
    {
        var d = Row(new float[] { 80, 70, 60, 55, 50, 10, 20, 14, 50, 40, 0 });
        DrainageField f = Bake(d, true);
        const int w = 11, a = w + 5, saddle = w + 6, b = w + 7;
        DrainageBasinInfo info = f.Basins.Span[f.BasinId[a]];
        Check(info.Closed && info.Overflows == 1 && f.Routed.Overflows.Length == 1, $"two pools: closed {info.Closed}, overflows {info.Overflows}, expected one overflow of a closed basin");
        if (f.Routed.Overflows.Length == 1)
        {
            DrainageOverflow o = f.Routed.Overflows[0];
            Check(o.SaddleCell == saddle && o.OutCell == b && o.HasLake && Close(o.Handover, 2d), $"two pools: overflow saddle {o.SaddleCell} (expected {saddle}), out {o.OutCell} (expected {b}), hand-over {o.Handover} (expected 2)");
            Check(o.Sill && !o.SamePool && !o.OtherPool && !o.SharedSaddle && o.RedirectRunoff == 0, "two pools: the saddle cell's own descent points into the full pool, so it is the pool's sill: its link turns to the receiving side and nothing is counted as redirected");
        }
        Check(f.Down[a] == saddle && f.Down[saddle] == b && f.Down[b] == -1, $"two pools: links A {f.Down[a]} (expected the saddle {saddle}), saddle {f.Down[saddle]} (expected B {b}), B {f.Down[b]} (expected -1)");
        Check(f.LakeId[a] >= 0 && f.Lakes.Span[f.LakeId[a]].Level == 20f && f.LakeId[b] >= 0 && f.Lakes.Span[f.LakeId[b]].Level == 18f, "two pools: levels are not 20 m (A, pinned at the saddle) and 18 m (B)");
        Check(!Cycle(f), "two pools: cycle in down");
        // Following down from any cell of the full pool reaches a wet cell of the receiving lake.
        int lakeB = f.LakeId[b];
        Check(Follow(f, a, c => f.LakeId[c] == lakeB && f.IsLakeWater(c)) == b, "two pools: following down from the full pool does not reach a wet cell of B's lake");
        // R: the pool passes its hand-over (2), the saddle cell's own runoff was counted in the pool, so R at the saddle is the hand-over; R at B is its own 1 plus that; area is plain.
        Check(Close(f.RunoffAccumulation[saddle], 2d), $"two pools: R at the saddle {f.RunoffAccumulation[saddle]}, expected the hand-over 2");
        Check(Close(f.RunoffAccumulation[b], 3d) && Close(f.RunoffAccumulation[b], f.Lakes.Span[lakeB].Inflow), $"two pools: R at B {f.RunoffAccumulation[b]}, expected its lake's inflow 3");
        Check(Close(f.RunoffAccumulation[b], f.Routed.Overflows[0].Handover + f.Runoff[b]), "two pools: R on the first cell after the saddle is not the hand-over plus the cell's own runoff");
        Check(f.AreaCells[b] == 8 && f.AreaCells[saddle] == 7, $"two pools: area at B {f.AreaCells[b]} (expected 8 = slope 4, rim, A, saddle, B), at the saddle {f.AreaCells[saddle]} (expected 7)");
        // The same domain without the overflow links: A is a pit, nothing crosses the saddle.
        DrainageField off = Bake(d, false);
        Check(off.Down[a] == -1 && off.Down[saddle] == a && off.Routed.Overflows.Length == 0, "two pools: with the links off, A must be a pit again");
        SameArithmetic(d, "two pools");
    }

    // ------------------------------------------------------------------ chain

    /// <summary>
    /// Row: slope 90 80 70 60 (cols 0-3), rim 50 (4), A 10 (5), saddle 30 (6), B 14 (7), saddle 20 (8), C 5 (9), rim 50 (10), 40 (11), sea (12). B has moisture 0.9375 (runoff 1.875, evaporation 0.625), C 0.25
    /// (runoff 0.5, evaporation 7.5). A: 4 + 1 + 1 + 1 = 7 against 5, hand-over 2 into B. B: 1.875 + 2 = 3.875 against 0.625, hand-over 3.25 into C. C: 0.5 + 1 (its saddle cell) + 3.25 = 4.75 against 7.5, a lake below its saddle (the rim at col 10 drains to the sea).
    /// </summary>
    private void Chain()
    {
        var d = Row(new float[] { 90, 80, 70, 60, 50, 10, 30, 14, 20, 5, 50, 40, 0 }, new Dictionary<int, float> { [7] = 0.9375f, [9] = 0.25f });
        DrainageField f = Bake(d, true);
        const int w = 13, a = w + 5, s1 = w + 6, b = w + 7, s2 = w + 8, c = w + 9;
        List<DrainageOverflow> list = f.Routed.Overflows.ToList();
        DrainageOverflow? oa = list.Cast<DrainageOverflow?>().FirstOrDefault(o => o!.Value.SaddleCell == s1), ob = list.Cast<DrainageOverflow?>().FirstOrDefault(o => o!.Value.SaddleCell == s2);
        Check(list.Count == 2 && oa != null && ob != null, $"chain: {list.Count} overflows, expected 2 (A over the saddle at col 6, B over the saddle at col 8)");
        if (oa != null && ob != null)
        {
            Check(oa.Value.OutCell == b && Close(oa.Value.Handover, 2d), $"chain: A passes {oa.Value.Handover} into {oa.Value.OutCell}, expected 2 into B ({b})");
            Check(ob.Value.OutCell == c && Close(ob.Value.Handover, 3.25), $"chain: B passes {ob.Value.Handover} into {ob.Value.OutCell}, expected 3.25 into C ({c}); B's includes A's");
        }
        Check(f.Down[a] == s1 && f.Down[s1] == b && f.Down[b] == s2 && f.Down[s2] == c && f.Down[c] == -1, $"chain: the path is A {f.Down[a]}, {f.Down[s1]}, B {f.Down[b]}, {f.Down[s2]}, C {f.Down[c]}; expected A, saddle, B, saddle, C, pit");
        Check(Follow(f, a, cell => cell == c) == c && f.LakeId[c] >= 0 && f.IsLakeWater(c), "chain: following down from A does not reach C's lake");
        if (oa != null && ob != null)
            Check(Close(f.RunoffAccumulation[b], oa.Value.Handover + f.Runoff[b]) && Close(f.RunoffAccumulation[c], ob.Value.Handover + f.Runoff[s2] + f.Runoff[c]), "chain: R on the first cell after a saddle is not the hand-over plus what else flows into it (for the second saddle: the saddle cell's own runoff and the cell's)");
        Check(Close(f.RunoffAccumulation[b], 3.875) && Close(f.RunoffAccumulation[s2], 4.25) && Close(f.RunoffAccumulation[c], 4.75), $"chain: R at B {f.RunoffAccumulation[b]}, at the second saddle {f.RunoffAccumulation[s2]}, at C {f.RunoffAccumulation[c]}; expected 3.875, 4.25 and 4.75");
        Check(f.LakeId[c] >= 0 && Close(f.Lakes.Span[f.LakeId[c]].Inflow, 4.75) && Close(f.RunoffAccumulation[c], f.Lakes.Span[f.LakeId[c]].Inflow), "chain: R at C's pit is not the inflow of its lake");
        Check(!Cycle(f), "chain: cycle in down");
        SameArithmetic(d, "chain");
    }

    // ------------------------------------------------------------------ two pools that merge

    /// <summary>Both hollows fill (more inflow: a 12-cell slope), the pools merge into one lake above the saddle: no hand-over, no path between them, and the links are those of the descent.</summary>
    private void Merge()
    {
        var heights = new float[7 + 12];
        for (int j = 0; j < 12; j++) heights[j] = 50 + (12 - j) * 5;
        new float[] { 50, 10, 20, 14, 50, 40, 0 }.CopyTo(heights, 12);
        var d = Row(heights);
        DrainageField on = Bake(d, true), off = Bake(d, false);
        int a = 19 + 13, saddle = 19 + 14, b = 19 + 15;
        Check(on.Routed.Overflows.Length == 0 && on.LakeId[a] >= 0 && on.LakeId[a] == on.LakeId[b] && on.LakeId[a] == on.LakeId[saddle], $"merge: {on.Routed.Overflows.Length} overflows, lakes {on.LakeId[a]}/{on.LakeId[saddle]}/{on.LakeId[b]}; expected none and one lake over both hollows");
        Check(on.Down.SequenceEqual(off.Down) && on.RunoffAccumulation.SequenceEqual(off.RunoffAccumulation) && on.AreaCells.SequenceEqual(off.AreaCells), "merge: links, R or area differ from the bake without overflows although no pool overflows");
        SameArithmetic(d, "merge");
    }

    // ------------------------------------------------------------------ a full pool with no room for a level

    /// <summary>
    /// Row: slope 80 70 60 55 (cols 0-3), rim 50 (4), A 10 (5), saddle 10.6 (6), B 5 (7), rim 50 (8), 40 (9), sea (10). A is full (7 against 5) but the whole-metre level has no room (floor(10.6) = 10 &lt; floor(10) + 1): no lake,
    /// where before this order the river that fills it ended on a dry pan (a Dead stem). Its cells link over the saddle into B.
    /// </summary>
    private void Pan()
    {
        var d = Row(new float[] { 80, 70, 60, 55, 50, 10, 10.6f, 5, 50, 40, 0 });
        DrainageField f = Bake(d, true);
        const int w = 11, a = w + 5, saddle = w + 6, b = w + 7;
        Check(f.LakeId[a] < 0 && f.Routed.Overflows.Length == 1 && !f.Routed.Overflows[0].HasLake && f.Report.OverflowPoolsNoLake == 1 && f.Report.OverflowLakes == 0, $"pan: lake at A {f.LakeId[a]}, overflows {f.Routed.Overflows.Length}; expected no lake and one overflow without a lake");
        Check(f.Down[a] == saddle && f.Down[saddle] == b, $"pan: links A {f.Down[a]} (expected the saddle {saddle}), saddle {f.Down[saddle]} (expected B {b})");
        Check(Follow(f, a, cell => f.LakeId[cell] >= 0 && f.IsLakeWater(cell)) == b, "pan: following down from A does not reach B's lake");
        Check(Close(f.RunoffAccumulation[b], f.Routed.Overflows[0].Handover + f.Runoff[saddle] + f.Runoff[b]), "pan: R on the first cell after the saddle is not the hand-over plus the saddle cell's own runoff (its descent leads to B, not into A) plus the cell's own");
        SameArithmetic(d, "pan");
        // Negative control: before the change A is a pit with no way out. The check above must fail on it.
        DrainageField off = Bake(d, false);
        Check(off.Down[a] == -1 && Follow(off, a, cell => f.LakeId[cell] >= 0 && off.IsLakeWater(cell)) < 0, "pan: negative control: with the links off, A should be a dead end that does not reach B (the saddle cell itself drains into B either way)");
    }

    // ------------------------------------------------------------------ a cell flows into the sill

    /// <summary>
    /// The two-pool row with one more cell above the saddle: row 2 col 6, 40 m, whose only neighbour is the saddle cell, so it drains through the saddle into A. A takes slope 4 + rim + A + saddle + that cell = 8 against 5
    /// and passes 3; B holds its own cell (1) plus 3 = 4. The saddle cell is the pool's sill, so after the links it passes the hand-over (3) and nothing else: the cell above it (R 1) is already part of that 3.
    /// Before the fix it passed 4. R at B is B's lake's inflow, 4.
    /// </summary>
    private void UpstreamOfSill()
    {
        var d = Row(new float[] { 80, 70, 60, 55, 50, 10, 20, 14, 50, 40, 0 }, null, new Dictionary<int, float> { [6] = 40f });
        DrainageField f = Bake(d, true);
        const int w = 11, a = w + 5, saddle = w + 6, b = w + 7, above = 2 * w + 6;
        Check(f.Routed.Overflows.Length == 1 && Close(f.Routed.Overflows[0].Handover, 3d) && f.Routed.Overflows[0].Sill, $"sill: {f.Routed.Overflows.Length} overflows, hand-over {(f.Routed.Overflows.Length > 0 ? f.Routed.Overflows[0].Handover : -1)}; expected one, 3, and the saddle cell a sill");
        Check(f.Down[above] == saddle && f.Down[a] == saddle && f.Down[saddle] == b, $"sill: links above {f.Down[above]}, A {f.Down[a]}, saddle {f.Down[saddle]}; expected the saddle, the saddle, B");
        Check(Close(f.RunoffAccumulation[saddle], 3d), $"sill: R at the saddle {f.RunoffAccumulation[saddle]}, expected the hand-over 3 and nothing else (the cell above it and the saddle's own runoff are part of it)");
        Check(f.LakeId[b] >= 0 && Close(f.Lakes.Span[f.LakeId[b]].Inflow, 4d) && Close(f.RunoffAccumulation[b], 4d), $"sill: R at B {f.RunoffAccumulation[b]}, expected its lake's inflow 4");
        Check(f.AreaCells[saddle] == 8 && f.AreaCells[b] == 9, $"sill: area at the saddle {f.AreaCells[saddle]} (expected 8) and at B {f.AreaCells[b]} (expected 9)");
        Check(!Cycle(f), "sill: cycle in down");
        SameArithmetic(d, "sill");
    }

    // ------------------------------------------------------------------ three pools meet in one saddle cell

    /// <summary>
    /// One saddle cell (row 1 col 6, 30 m) with three hollows around it: A (col 5, 10 m), B (col 7, 14 m, moisture 0.9375: runoff 1.875, evaporation 0.625) and C (row 2 col 6, 12 m). A takes slope 4 + rim + A + saddle = 7 against 5
    /// and passes 2 to C; B passes 1.25 into A's pool (the lowest-key neighbour of the saddle on the other side is A), which therefore passes 1.25 more: A's hand-over is 3.25, B's 1.25, C holds 1 + 3.25 = 4.25. B hands over into the pool
    /// of an overflow with the same saddle cell, so its 1.25 is not added at the saddle again: R at the saddle is 3.25, R at C is 4.25 (its lake's inflow). Before the fix the saddle carried 4.5 and C 5.5.
    /// </summary>
    private void ThreePools()
    {
        var d = Row(new float[] { 80, 70, 60, 55, 50, 10, 30, 14, 50, 40, 0 }, new Dictionary<int, float> { [7] = 0.9375f }, new Dictionary<int, float> { [6] = 12f });
        DrainageField f = Bake(d, true);
        const int w = 11, a = w + 5, saddle = w + 6, b = w + 7, c = 2 * w + 6;
        List<DrainageOverflow> list = f.Routed.Overflows.ToList();
        DrainageOverflow? oa = list.Cast<DrainageOverflow?>().FirstOrDefault(o => o!.Value.OutCell == c), ob = list.Cast<DrainageOverflow?>().FirstOrDefault(o => o!.Value.OutCell == a);
        Check(list.Count == 2 && oa != null && ob != null && list.All(o => o.SaddleCell == saddle && o.SharedSaddle), $"three pools: {list.Count} overflows at the shared saddle; expected A into C and B into A");
        if (oa != null && ob != null) Check(Close(oa.Value.Handover, 3.25) && Close(ob.Value.Handover, 1.25), $"three pools: hand-overs {oa.Value.Handover} (A) and {ob.Value.Handover} (B), expected 3.25 and 1.25");
        Check(f.Down[a] == saddle && f.Down[b] == saddle && f.Down[saddle] == c && f.Down[c] == -1, $"three pools: links A {f.Down[a]}, B {f.Down[b]}, saddle {f.Down[saddle]} (expected C {c})");
        Check(Close(f.RunoffAccumulation[saddle], 3.25), $"three pools: R at the saddle {f.RunoffAccumulation[saddle]}, expected 3.25 (B's hand-over is part of A's)");
        Check(f.LakeId[c] >= 0 && Close(f.Lakes.Span[f.LakeId[c]].Inflow, 4.25) && Close(f.RunoffAccumulation[c], 4.25), $"three pools: R at C {f.RunoffAccumulation[c]}, expected its lake's inflow 4.25");
        Check(!Cycle(f), "three pools: cycle in down");
        SameArithmetic(d, "three pools");
    }

    // ------------------------------------------------------------------ hand-calculated fixtures of the balance test on both settings

    private void Controls()
    {
        // The fixtures of drainage_graph_test's balance section, with the links on and off: same lakes, same balance.
        const int w = 11;
        foreach (float moisture in new[] { 0.375f, 0.71875f, 0.875f })
        {
            var h = new float[3 * w];
            var kind = Enumerable.Repeat(DrainageCellKind.Barrier, h.Length).ToArray();
            float[] row = { 40, 30, 20, 10, 12, 30, 20, 8, 9, 12, 0 };
            for (int i = 0; i < w; i++) { h[i] = 1000; h[2 * w + i] = 1000; h[w + i] = row[i]; kind[w + i] = i == w - 1 ? DrainageCellKind.Ocean : DrainageCellKind.Land; }
            var chain = new DrainageDomain(w, 3, false, 1000d, 1000d, kind, h, h, Enumerable.Repeat(moisture, h.Length).ToArray());
            SameArithmetic(chain, $"balance chain {moisture}");
        }
    }
}
