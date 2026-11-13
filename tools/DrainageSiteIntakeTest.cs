using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Hydrology;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-19 (docs/work-orders/terrain-vegetation/T2-rules.md, Spec RS2): site intakes on synthetic domains. A valley slopes into a Barrier block. With the option off (today) a lake stands against the block above the block's ground;
/// with it on the block's edge becomes outlet cells, no lake stands above that ground and the valley drains to an Intake terminal. Every Barrier cell that touches land becomes Intake and no Land cell touches a Barrier cell
/// (also across the wrap seam), interior Barrier cells stay blocked, the mass balance closes with the intake outflow counted, and a depression beside the block that is lower than the block's ground keeps its lake.
/// T2R-23 (Part A) adds: a depression against the block whose lowest rim cell is an edge cell holds no lake and passes its whole inflow on (and keeps its lake when the inflow is too small to spill), a valley on the far side of a block carries only
/// its own runoff, the mass balance closes with the site term, and the chain method with the removal rules on one hand-built field (rules None, Branch and Catchment).
/// Prints DRAINAGE_SITE_INTAKE=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests drainage_site_intake_test.
/// </summary>
public partial class DrainageSiteIntakeTest : RefCounted
{
    private const int W = 14, H = 7, BlockFirstColumn = 10;
    private const float BlockGround = 25f;
    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }

    public int Run()
    {
        try
        {
            Valleys();
            Conversion();
            SiteDrained();
            FarSide();
            ChainRemoval();
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_SITE_INTAKE=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("DRAINAGE_SITE_INTAKE=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("DRAINAGE_SITE_INTAKE=PASS");
        return 0;
    }

    /// <summary>Sea at (0, 3) behind a pass in column 1; the valley (columns 1 to 9, rows 1 to 5) slopes down to a Barrier block of ground 25 m (columns 10 to 13, rows 1 to 5); ridges of 1,000 m all round.</summary>
    private static (float[] Height, DrainageCellKind[] Kind) Valley(float[] profile)
    {
        var h = new float[W * H];
        var kind = new DrainageCellKind[W * H];
        for (int row = 0; row < H; row++)
            for (int col = 0; col < W; col++)
            {
                int i = row * W + col;
                h[i] = 1000f; kind[i] = DrainageCellKind.Land;
                if (row is >= 1 and <= 5 && col is >= 1 and <= 9) h[i] = profile[col - 1] + 5f * Math.Abs(row - 3);
                if (row is >= 1 and <= 5 && col >= BlockFirstColumn) { h[i] = BlockGround; kind[i] = DrainageCellKind.Barrier; }
            }
        h[3 * W] = 0f; kind[3 * W] = DrainageCellKind.Ocean;
        return (h, kind);
    }

    private static DrainageField Bake(float[] h, DrainageCellKind[] kind, double moisture)
    {
        var d = new DrainageDomain(W, H, false, 1000d, 1000d, kind, h, h, Enumerable.Repeat((float)moisture, h.Length).ToArray());
        return DrainageBake.BakeDomain(null, d, 0.5d, 1, null, CancellationToken.None, 0, 0, 0, 0d, 10d / DrainageLakeBalance.E0);
    }

    /// <summary>Largest (lake level minus ground of a block or intake cell next to a lake cell) over all lake cells; negative infinity when no lake touches one.</summary>
    private static double LakeOverEdge(DrainageField f)
    {
        double worst = double.NegativeInfinity;
        for (int cell = 0; cell < f.Count; cell++)
        {
            int lake = f.LakeId[cell];
            if (lake < 0) continue;
            for (int dir = 0; dir < 8; dir++)
            {
                int n = f.Domain.GeometricNeighbor(cell, dir);
                if (n < 0 || f.Kind[n] is not (DrainageCellKind.Barrier or DrainageCellKind.Intake)) continue;
                worst = Math.Max(worst, f.Lakes.Span[lake].Level - f.Lowest[n]);
            }
        }
        return worst;
    }

    private static int Count(DrainageField f, DrainageCellKind k)
    {
        int n = 0;
        for (int i = 0; i < f.Count; i++) if (f.Kind[i] == k) n++;
        return n;
    }

    private static double MaxLevel(DrainageField f)
    {
        double level = double.NegativeInfinity;
        foreach (DrainageLake lake in f.Lakes.Span) level = Math.Max(level, lake.Level);
        return level;
    }

    private static int Wet(DrainageField f)
    {
        int n = 0;
        for (int i = 0; i < f.Count; i++) if (f.IsLakeWater(i)) n++;
        return n;
    }

    private void Valleys()
    {
        // A: the pass (80 m) is above the block's ground, so the valley fills to it against the block today.
        float[] steep = { 80, 40, 34, 30, 26, 22, 18, 14, 10 };
        (float[] h, DrainageCellKind[] kind) = Valley(steep);
        DrainageField off = Bake(h, (DrainageCellKind[])kind.Clone(), 0.9375d);
        double offOver = LakeOverEdge(off);
        Check(offOver > 0d, $"option off: no lake stands over the block's ground (largest {offOver:F1} m); the check cannot fail");

        DrainageCellKind[] onKind = (DrainageCellKind[])kind.Clone();
        int intakes = DrainageBarriers.MarkIntakes(W, H, false, onKind);
        DrainageField on = Bake(h, onKind, 0.9375d);
        double onOver = LakeOverEdge(on);
        Check(onOver <= 0d, $"intakes on: a lake stands {onOver:F1} m over intake ground");
        Check(intakes > 0 && Count(on, DrainageCellKind.Intake) == intakes, "intake cell count");
        foreach (int col in new[] { 2, 5, 8 })
            Check(on.Terminal[3 * W + col] == DrainageCellKind.Intake, $"valley cell {col} has terminal {on.Terminal[3 * W + col]}, expected Intake");
        Check(on.Report.MassError <= 1e-9 && on.Report.Conserved, $"mass balance with intakes: error {on.Report.MassError:E2}, conserved {on.Report.Conserved}");
        Check(on.Report.OutflowRunoff > 0d, "no runoff reaches an outlet with intakes on");
        Check(on.Down[3 * W + BlockFirstColumn] == -1, "an intake cell has a down link");
        Check(off.Report.MassError <= 1e-9, $"mass balance with the option off: error {off.Report.MassError:E2}");
        Check(Count(off, DrainageCellKind.Land) == Count(on, DrainageCellKind.Land), "land cell counts differ");

        // B: a depression whose pass (20 m) is below the block's ground keeps its lake and does not drain into the block. Low moisture so that it is closed.
        float[] low = { 20, 19, 18, 17, 16, 15, 14, 13, 12 };
        (float[] hb, DrainageCellKind[] kb) = Valley(low);
        DrainageCellKind[] kbOn = (DrainageCellKind[])kb.Clone();
        DrainageBarriers.MarkIntakes(W, H, false, kbOn);
        DrainageField b = Bake(hb, kbOn, 0.375d);
        int wet = Wet(b);
        Check(wet > 0, "the depression lower than the block's ground lost its lake");
        Check(LakeOverEdge(b) <= 0d, $"depression beside the block: lake {LakeOverEdge(b):F1} m over edge ground");
        Check(b.Terminal[3 * W + 9] != DrainageCellKind.Intake, "the depression lower than the block drains into an intake");
        // The ridge cells beside the block now drain into it, so the catchment of the closed lake shrinks a little and its balance level may drop; it never rises and stays under the pass.
        DrainageField bOff = Bake(hb, kb, 0.375d);
        Check(Wet(bOff) > 0 && MaxLevel(b) <= MaxLevel(bOff) + 1e-6 && MaxLevel(b) <= 20d + 1e-6, $"the lower depression's lake level with the option {MaxLevel(b):F2} m, without {MaxLevel(bOff):F2} m (the pass is at 20 m)");
    }

    private void Conversion()
    {
        // Every Barrier cell that touches land is Intake; no Land cell touches a Barrier cell; interior Barrier cells stay; the other cells are unchanged.
        const int w = 8, h = 6;
        var kind = new DrainageCellKind[w * h];
        for (int row = 1; row < 5; row++) for (int col = 2; col < 7; col++) kind[row * w + col] = DrainageCellKind.Barrier;
        kind[0] = DrainageCellKind.Ocean;
        DrainageCellKind[] flat = (DrainageCellKind[])kind.Clone();
        int count = DrainageBarriers.MarkIntakes(w, h, false, flat);
        // The 5 x 4 block has a ring of 14 edge cells and a 3 x 2 interior.
        Check(count == 14, $"expected 14 intake cells, got {count}");
        for (int cell = 0; cell < flat.Length; cell++)
        {
            if (flat[cell] == DrainageCellKind.Land)
                for (int dir = 0; dir < 8; dir++)
                {
                    int row = cell / w + DrainageDomain.Dy[dir], col = cell % w + DrainageDomain.Dx[dir];
                    if ((uint)row < h && (uint)col < w) Check(flat[row * w + col] != DrainageCellKind.Barrier, $"land cell {cell} touches a barrier cell");
                }
            if (kind[cell] == DrainageCellKind.Barrier)
            {
                int row = cell / w, col = cell % w;
                bool interior = row is >= 2 and <= 3 && col is >= 3 and <= 5;
                Check((flat[cell] == DrainageCellKind.Barrier) == interior, $"cell {cell} (row {row}, col {col}) is {flat[cell]}");
            }
            else Check(flat[cell] == kind[cell], $"cell {cell} changed from {kind[cell]} to {flat[cell]}");
        }
        // The seam: with the wrap a Barrier cell in column 0 touches the Land cells of the last column.
        const int sw = 6, sh = 3;
        var seam = new DrainageCellKind[sw * sh];
        for (int row = 0; row < sh; row++) for (int col = 0; col < sw; col++) seam[row * sw + col] = col < 5 ? DrainageCellKind.Barrier : DrainageCellKind.Land;
        DrainageCellKind[] noWrap = (DrainageCellKind[])seam.Clone(), withWrap = (DrainageCellKind[])seam.Clone();
        int a = DrainageBarriers.MarkIntakes(sw, sh, false, noWrap), b = DrainageBarriers.MarkIntakes(sw, sh, true, withWrap);
        Check(a == 3 && b == 6, $"seam fixture intake counts {a} (expected 3) / {b} (expected 6)");
        Check(withWrap[0] == DrainageCellKind.Intake && noWrap[0] == DrainageCellKind.Barrier, "the seam neighbour was not read across the wrap only with the wrap");
    }

    private static double RunoffToIntakes(DrainageField f)
    {
        double expected = 0;
        for (int cell = 0; cell < f.Count; cell++) if (f.Kind[cell] is DrainageCellKind.Land && f.Terminal[cell] == DrainageCellKind.Intake) expected += f.Runoff[cell];
        return expected;
    }

    private static double AccumulatedAtIntakes(DrainageField f)
    {
        double actual = 0;
        for (int cell = 0; cell < f.Count; cell++) if (f.Kind[cell] == DrainageCellKind.Intake) actual += f.RunoffAccumulation[cell];
        return actual;
    }

    private void SiteDrained()
    {
        // A bowl (floor 8 to 18 m) against the block (ground 25 m): its lowest rim cell is an edge cell, so with the option it spills into the site.
        float[] bowl = { 80, 40, 34, 30, 8, 8, 8, 8, 8 };
        (float[] h, DrainageCellKind[] kind) = Valley(bowl);
        DrainageField off = Bake(h, (DrainageCellKind[])kind.Clone(), 0.9375d);
        Check(Wet(off) > 0 && LakeOverEdge(off) > 0d, "site drained: with the option off no lake stands against the block, the check cannot fail");
        DrainageCellKind[] onKind = (DrainageCellKind[])kind.Clone();
        DrainageBarriers.MarkIntakes(W, H, false, onKind);
        DrainageField on = Bake(h, onKind, 0.9375d);
        Check(on.Lakes.Length == 0 && Wet(on) == 0, $"site drained: {on.Lakes.Length} lakes, {Wet(on)} wet cells stand with the option on");
        int drained = 0, drainedBasin = -1;
        for (int k = 0; k < on.Basins.Length; k++) if (on.Basins.Span[k].SiteDrained) { drained++; drainedBasin = k; }
        Check(drained == 1, $"site drained: {drained} site-drained basins, expected 1");
        if (drainedBasin >= 0)
        {
            DrainageBasinInfo info = on.Basins.Span[drainedBasin];
            Check(!info.Closed && info.Lakes == 0 && info.Evaporation == 0d && Math.Abs(info.Outflow - info.Inflow) <= 1e-9 * Math.Max(1d, info.Inflow), $"site drained: info closed {info.Closed}, lakes {info.Lakes}, evaporation {info.Evaporation}, outflow {info.Outflow} vs inflow {info.Inflow}");
            int cells = 0;
            for (int cell = 0; cell < on.Count; cell++)
                if (on.BasinId[cell] == drainedBasin) { cells++; Check(on.Terminal[cell] == DrainageCellKind.Intake && on.LakeId[cell] < 0, $"site drained: basin cell {cell} has terminal {on.Terminal[cell]}, lake {on.LakeId[cell]}"); }
            Check(cells == info.Cells && cells > 0, $"site drained: {cells} basin cells, info says {info.Cells}");
        }
        // R at the edge cells equals the runoff of everything that drains to a site, with no evaporation taken off.
        double expected = RunoffToIntakes(on), actual = AccumulatedAtIntakes(on);
        Check(expected > 0d && Math.Abs(actual - expected) <= 1e-9 * expected, $"site drained: R at the edge cells {actual:F9}, runoff of the cells that drain there {expected:F9}");
        Check(on.Report.MassError <= 1e-9 && on.Report.Conserved, $"site drained: mass balance error {on.Report.MassError:E2}, conserved {on.Report.Conserved}");
        Check(on.Report.SiteDrainedBasins == 1 && Math.Abs(on.Report.SiteRunoff - actual) <= 1e-9 * Math.Max(1d, actual), $"site drained: report counts {on.Report.SiteDrainedBasins} basins, site runoff {on.Report.SiteRunoff} vs {actual}");
        // Too little inflow to spill: the basin is closed and keeps its balance lake.
        DrainageField dry = Bake(h, (DrainageCellKind[])onKind.Clone(), 0.375d);
        int closed = 0, siteBasins = 0;
        foreach (DrainageBasinInfo info in dry.Basins.Span) { if (info.Closed) closed++; if (info.SiteDrained) siteBasins++; }
        Check(closed >= 1 && siteBasins == 0 && Wet(dry) > 0, $"site drained, low inflow: closed basins {closed}, site-drained {siteBasins}, wet cells {Wet(dry)}");
        Check(dry.Report.MassError <= 1e-9, $"site drained, low inflow: mass balance error {dry.Report.MassError:E2}");
    }

    private void FarSide()
    {
        // One row: the near side (columns 0 to 3) drains into the edge cell of a block (columns 4 to 6), the far side (columns 7 to 10, lower than the block's ground) runs on to the sea in column 11 and carries its own runoff only.
        const int w = 12;
        var kind = new DrainageCellKind[w];
        float[] heights = { 70, 60, 50, 40, 25, 25, 25, 24, 20, 16, 12, 0 };
        for (int c = 4; c <= 6; c++) kind[c] = DrainageCellKind.Barrier;
        kind[11] = DrainageCellKind.Ocean;
        DrainageBarriers.MarkIntakes(w, 1, false, kind);
        var d = new DrainageDomain(w, 1, false, 1000d, 1000d, kind, heights, (float[])heights.Clone(), Enumerable.Repeat(0.9375f, w).ToArray());
        DrainageField f = DrainageBake.BakeDomain(null, d, 0.5d, 1, null, CancellationToken.None, 0, 0, 0, 0d, 10d / DrainageLakeBalance.E0);
        double r = f.CellAreaKm2 * Math.Max(0.1, 0.9375d / 0.5d);
        for (int c = 7; c <= 10; c++) Check(Math.Abs(f.RunoffAccumulation[c] - (c - 6) * r) <= 1e-9, $"far side: R at column {c} is {f.RunoffAccumulation[c]:F6}, the far side alone gives {(c - 6) * r:F6}");
        Check(Math.Abs(f.RunoffAccumulation[4] - 4 * r) <= 1e-9 && f.RunoffAccumulation[6] == 0d, $"far side: R at the near edge cell {f.RunoffAccumulation[4]:F6} (expected {4 * r:F6}), at the far edge cell {f.RunoffAccumulation[6]:F6}");
        Check(Math.Abs(f.RunoffAccumulation[11] - 4 * r) <= 1e-9, $"far side: the sea receives {f.RunoffAccumulation[11]:F6}, expected {4 * r:F6}");
        Check(f.Report.MassError <= 1e-9 && f.Report.Conserved, $"far side: mass balance error {f.Report.MassError:E2}");
    }

    /// <summary>
    /// The chain method (RiverGraphBuilder.FindChannels) on one hand-built field, 12 x 7 cells, threshold 1 cell: row 3 holds river i1 (columns 1 and 2) into lake A (columns 3 and 4), then the trunk T (columns 5 to 8, the cells in columns 7 and 8 lie in a site-drained basin)
    /// to the edge cell in column 9. t1 (column 6, rows 0 to 2) joins T and t2 (row 1, columns 4 and 5) joins t1; V (column 7, rows 5 and 4) joins T inside the basin; U (row 6) reaches the sea.
    /// </summary>
    private void ChainRemoval()
    {
        const int w = 12, h = 7;
        static int Id(int row, int col) => row * w + col;
        var kind = new DrainageCellKind[w * h];
        var terminal = new DrainageCellKind[w * h];
        var down = new int[w * h];
        var area = new int[w * h];
        var lakeId = new int[w * h];
        var visited = new bool[w * h];
        var lowest = new float[w * h];
        Array.Fill(down, -1); Array.Fill(lakeId, -1); Array.Fill(lowest, 200f); Array.Fill(terminal, DrainageCellKind.Barrier);
        void Cell(int row, int col, int next, int cells, DrainageCellKind end)
        {
            int c = Id(row, col);
            down[c] = next; area[c] = cells; visited[c] = true; terminal[c] = end;
        }
        // i1 and lake A, then T.
        Cell(3, 1, Id(3, 2), 2, DrainageCellKind.Intake); Cell(3, 2, Id(3, 3), 3, DrainageCellKind.Intake);
        Cell(3, 3, Id(3, 4), 8, DrainageCellKind.Intake); Cell(3, 4, Id(3, 5), 9, DrainageCellKind.Intake);
        lakeId[Id(3, 3)] = lakeId[Id(3, 4)] = 0; lowest[Id(3, 3)] = lowest[Id(3, 4)] = 90f;
        Cell(3, 5, Id(3, 6), 10, DrainageCellKind.Intake); Cell(3, 6, Id(3, 7), 18, DrainageCellKind.Intake);
        Cell(3, 7, Id(3, 8), 22, DrainageCellKind.Intake); Cell(3, 8, Id(3, 9), 23, DrainageCellKind.Intake);
        kind[Id(3, 9)] = DrainageCellKind.Intake; terminal[Id(3, 9)] = DrainageCellKind.Intake; visited[Id(3, 9)] = true;
        // t1 and t2, V.
        Cell(0, 6, Id(1, 6), 3, DrainageCellKind.Intake); Cell(1, 6, Id(2, 6), 6, DrainageCellKind.Intake); Cell(2, 6, Id(3, 6), 7, DrainageCellKind.Intake);
        Cell(1, 4, Id(1, 5), 1, DrainageCellKind.Intake); Cell(1, 5, Id(1, 6), 2, DrainageCellKind.Intake);
        Cell(5, 7, Id(4, 7), 2, DrainageCellKind.Intake); Cell(4, 7, Id(3, 7), 3, DrainageCellKind.Intake);
        // U to the sea.
        Cell(6, 1, Id(6, 2), 1, DrainageCellKind.Ocean); Cell(6, 2, Id(6, 3), 2, DrainageCellKind.Ocean); Cell(6, 3, Id(6, 4), 3, DrainageCellKind.Ocean);
        kind[Id(6, 4)] = DrainageCellKind.Ocean; terminal[Id(6, 4)] = DrainageCellKind.Ocean; visited[Id(6, 4)] = true;
        var siteDrained = new bool[w * h];
        siteDrained[Id(3, 7)] = siteDrained[Id(3, 8)] = true;
        var domain = new DrainageDomain(w, h, false, 1000d, 1000d, kind, lowest, (float[])lowest.Clone(), Enumerable.Repeat(0.5f, w * h).ToArray());
        var field = new RiverGraphBuilder.ChainField { Domain = domain, Down = down, Area = area, LakeId = lakeId, Kind = kind, Terminal = terminal, Visited = visited, Lowest = lowest, LakeLevel = new[] { 100f }, CellArea = 1d };
        int StemOf(RiverGraphBuilder.ChainResult r, int row, int col) => r.Chains.FindIndex(c => c[0] == Id(row, col));
        bool Contains(RiverGraphBuilder.ChainResult r, int row, int col) => r.Chains.Any(c => c.Contains(Id(row, col)));

        // None: nothing is removed; T ends at the edge cell with terminal Intake (the check can fail).
        RiverGraphBuilder.ChainResult none = RiverGraphBuilder.FindChannels(field, 1d, new RiverBuildOptions(), siteDrained, out RiverSiteRemovalStats noneStats, out bool[]? noneRemoved);
        Check(noneRemoved == null && noneStats.RemovedStems == 0, "chains, rule None: something was removed");
        Check(none.Chains.Count == 6 && none.TerminalCounts[(int)RiverTerminal.Intake] == 1, $"chains, rule None: {none.Chains.Count} stems (expected 6), {none.TerminalCounts[(int)RiverTerminal.Intake]} ending at an intake (expected 1)");
        int tNone = StemOf(none, 3, 3);
        Check(tNone >= 0 && none.Terminals[tNone] == RiverTerminal.Intake && none.Chains[tNone].Length == 6, "chains, rule None: T does not run from lake A to the edge cell");

        // Branch: T and V are removed; t1 is Cut, t2 still joins t1; i1 ends in lake A, which has no outlet; U is unchanged.
        RiverGraphBuilder.ChainResult branch = RiverGraphBuilder.FindChannels(field, 1d, new RiverBuildOptions { SiteRemoval = RiverSiteRemovalRule.Branch }, siteDrained, out RiverSiteRemovalStats branchStats, out bool[]? branchRemoved);
        Check(branchStats.RemovedStems == 2 && branchStats.ByReason[RiverSiteRemoval.ReasonEndsAtSite] == 1 && branchStats.ByReason[RiverSiteRemoval.ReasonSiteDrainedInlet] == 1, $"chains, Branch: removed {branchStats.RemovedStems} stems (by reason {branchStats.ByReason[1]}/{branchStats.ByReason[2]}), expected T and V");
        Check(branchRemoved != null && branchRemoved[Id(3, 6)] && branchRemoved[Id(4, 7)] && !branchRemoved[Id(2, 6)] && !branchRemoved[Id(3, 2)], "chains, Branch: the removed cells are not those of T and V");
        Check(branch.TerminalCounts[(int)RiverTerminal.Intake] == 0 && branch.TerminalCounts[(int)RiverTerminal.Dead] == 0 && branch.TerminalCounts[(int)RiverTerminal.Cut] == 1, $"chains, Branch: terminals intake {branch.TerminalCounts[(int)RiverTerminal.Intake]}, dead {branch.TerminalCounts[(int)RiverTerminal.Dead]}, cut {branch.TerminalCounts[(int)RiverTerminal.Cut]}");
        int t1 = StemOf(branch, 0, 6), t2 = StemOf(branch, 1, 4), i1 = StemOf(branch, 3, 1), aStem = StemOf(branch, 3, 3), u = StemOf(branch, 6, 1);
        Check(t1 >= 0 && branch.Terminals[t1] == RiverTerminal.Cut && branch.Chains[t1].Length == 3, "chains, Branch: t1 is not a Cut stem of its own three cells");
        Check(t2 >= 0 && t1 >= 0 && branch.Terminals[t2] == RiverTerminal.Confluence && branch.ReceiverStems[t2] == t1, "chains, Branch: t2 does not join t1");
        Check(aStem >= 0 && i1 >= 0 && branch.Terminals[aStem] == RiverTerminal.Lake && branch.TerminalIds[aStem] == 0 && branch.Terminals[i1] == RiverTerminal.Confluence && branch.ReceiverStems[i1] == aStem, "chains, Branch: i1 does not end in lake A");
        Check(!Contains(branch, 3, 5) && !Contains(branch, 3, 6) && !Contains(branch, 3, 7) && !Contains(branch, 3, 8) && !Contains(branch, 5, 7), "chains, Branch: lake A still has an outlet river or a removed cell is a channel");
        Check(u >= 0 && branch.Terminals[u] == RiverTerminal.Sea && branch.Chains[u].Length == 3 && branch.Chains.Count == 5, $"chains, Branch: U changed or the stem count is {branch.Chains.Count} (expected 5)");

        // Catchment: T, V, t1, t2 and i1 are removed, no stem is Cut, lake A stays (without rivers), U is unchanged.
        RiverGraphBuilder.ChainResult catchment = RiverGraphBuilder.FindChannels(field, 1d, new RiverBuildOptions { SiteRemoval = RiverSiteRemovalRule.Catchment }, siteDrained, out _, out bool[]? catchmentRemoved);
        Check(catchmentRemoved == null && catchment.Chains.Count == 1 && catchment.TerminalCounts[(int)RiverTerminal.Sea] == 1 && catchment.Chains[0][0] == Id(6, 1), $"chains, Catchment: {catchment.Chains.Count} stems, expected U alone");
        Check(catchment.TerminalCounts[(int)RiverTerminal.Cut] == 0 && catchment.TerminalCounts[(int)RiverTerminal.Dead] == 0 && catchment.TerminalCounts[(int)RiverTerminal.Intake] == 0, "chains, Catchment: a Cut, Dead or Intake stem");
        Check(!catchment.LakeMarked[Id(3, 3)] && lakeId[Id(3, 3)] == 0, "chains, Catchment: lake A is marked by a river or gone");
    }
}
