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
}
