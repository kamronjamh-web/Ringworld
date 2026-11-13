using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-19 (docs/work-orders/terrain-vegetation/T2-rules.md, Spec RS2): the <c>--site-intakes</c> measurement. It needs no spans, so it runs with <c>--no-levels</c>. The bake itself changes only with
/// <c>--site-intakes</c> (the edge of every keep-out becomes an outlet) and <c>--keep-out=&lt;m&gt;</c> (the width beyond a structure's influence radius, default 650 m); this section only reads.
/// Without <c>--site-intakes</c> it measures today's rule, with the Barrier cells that touch land standing in for the intake cells. Every line is "key | value" so the runs can be tabulated.
/// </summary>
public partial class DrainageBakeReportTool
{
    private static string SiteIntakeReport(RiverNetwork net, DrainageField field, DrainageGrid grid, TerrainGenerationParameters recipe, RiverBuildReport rivers, bool intakesOn, double keepOut)
    {
        var sb = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        DrainageGridSpec spec = grid.Spec;
        DrainageBakeReport rep = field.Report;
        int width = field.Width, height = field.Height;
        long circumference = spec.CircumferenceMeters;
        double ringM = circumference;
        void Row(string key, string value) => sb.AppendLine(key + " | " + value);
        string F(double v, string fmt = "F1") => v.ToString(fmt, ci);

        EmitterSiteTerrainShaper? shaper = recipe.HasEmitterSites ? EmitterSiteTerrainShaper.GetOrCreate(recipe) : null;
        ClimateMachine[] machines = shaper?.Climate != null ? shaper.Climate.Machines.ToArray() : Array.Empty<ClimateMachine>();
        double machineHalf = shaper?.Climate != null ? shaper.Climate.Recipe.ForecourtLengthM * 0.5 : 0, machineInfluence = shaper?.Climate != null ? shaper.Climate.Recipe.InfluenceRadiusM : 0;
        bool hasLung = shaper?.WeatherLung != null;
        double lungHx = hasLung ? shaper!.WeatherLung!.Recipe.BoundHalfXM : 0, lungHy = hasLung ? shaper!.WeatherLung!.Recipe.BoundHalfYM : 0;
        long lungA = hasLung ? shaper!.WeatherLung!.Site.Around : 0; int lungC = hasLung ? shaper!.WeatherLung!.Site.Across : 0;

        // Nearest structure at a point: 0 emitter site, 1 Wall Engine stadium, 2 Weather Lung; the clearance is the distance to its influence boundary (the keep-out is measured from it).
        (int Class, int Index, double Clearance) Nearest(double around, double across)
        {
            int cls = -1, index = -1;
            double best = double.PositiveInfinity;
            if (shaper != null && shaper.Registry.TryGetNearestSite((long)Math.Floor(around), across, out int site, out double du, out double dw))
            { best = Math.Sqrt(du * du + dw * dw) - shaper.Registry[site].InfluenceRadius; cls = 0; index = site; }
            for (int i = 0; i < machines.Length; i++)
            {
                double da = Math.Abs(around - machines[i].Around); da = Math.Min(da, ringM - da);
                double sx = Math.Max(0d, da - machineHalf), sy = across - machines[i].Across;
                double c = Math.Sqrt(sx * sx + sy * sy) - machineInfluence;
                if (c < best) { best = c; cls = 1; index = i; }
            }
            if (hasLung)
            {
                double da = Math.Abs(around - lungA); da = Math.Min(da, ringM - da);
                double ex = Math.Max(0d, da - lungHx), ey = Math.Max(0d, Math.Abs(across - lungC) - lungHy);
                double c = Math.Sqrt(ex * ex + ey * ey);
                if (c < best) { best = c; cls = 2; index = 0; }
            }
            return (cls, index, best);
        }

        Row("mode", intakesOn ? $"site intakes on, keep-out {F(keepOut, "F0")} m" : $"option off (today's rule, keep-out {F(keepOut, "F0")} m)");
        Row("barrier cells", rep.BarrierCells.ToString(ci));
        Row("intake cells", rep.IntakeCells.ToString(ci));

        // ------------------------------------------------------------------ stems
        int ends = 0, sea = 0, intakeStems = 0;
        var qs = new List<double>(); var widths = new List<double>(); var widthsFree = new List<double>();
        var siteOfStem = new HashSet<int>(); int engineEnds = 0, lungEnds = 0, siteEnds = 0;
        RiverDangerConstants free = RiverDangerConstants.Default with { MinDepth = 0d, MinWidth = 0d };
        double intakeQ = 0, allEndQ = 0;
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStem st = net.Stems[i];
            if (st.Terminal == RiverTerminal.Confluence) continue;
            ends++;
            if (st.Terminal == RiverTerminal.Sea) sea++;
            RiverReach last = net.Reaches[st.ReachStart + st.ReachCount - 1];
            allEndQ += last.Q;
            if (st.Terminal != RiverTerminal.Intake) continue;
            intakeStems++;
            qs.Add(last.Q); intakeQ += last.Q;
            widths.Add(RiverDanger.Channel(RiverDangerConstants.Default, last.Q, last.S, net.ChannelDepth, net.Ferocity).Width);
            widthsFree.Add(RiverDanger.Channel(free, last.Q, last.S, net.ChannelDepth, net.Ferocity).Width);
            double a = net.NodeAroundDm[st.NodeStart + st.NodeCount - 1] / 10d, c = net.NodeAcrossDm[st.NodeStart + st.NodeCount - 1] / 10d;
            (int cls, int index, _) = Nearest(a, c);
            if (cls == 0) { siteEnds++; siteOfStem.Add(index); } else if (cls == 1) engineEnds++; else if (cls == 2) lungEnds++;
        }
        Row("stems", net.Stems.Length.ToString(ci));
        Row("river ends (stems that do not join another river)", ends.ToString(ci));
        Row("stems ending at an intake", intakeStems.ToString(ci));
        Row("share of river ends that end at an intake", F(100d * intakeStems / Math.Max(1, ends)) + " %");
        qs.Sort(); widths.Sort(); widthsFree.Sort();
        Row("intake stem discharge p50 / p90 / sum (m3/s)", $"{F(Quantile(qs, 0.5), "F2")} / {F(Quantile(qs, 0.9), "F2")} / {F(intakeQ, "F1")}");
        Row("intake stem width p50 / p90 / max (m), T3R-3 law with the 8 m floor", $"{F(Quantile(widths, 0.5))} / {F(Quantile(widths, 0.9))} / {F(Quantile(widths, 1))}");
        Row("intake stem width p50 / p90 / max (m), the same law without the floors", $"{F(Quantile(widthsFree, 0.5))} / {F(Quantile(widthsFree, 0.9))} / {F(Quantile(widthsFree, 1))}");
        Row("intake discharge as a share of the discharge of all river ends", F(100d * intakeQ / Math.Max(1e-9, allEndQ)) + " %");
        int sitesTotal = shaper?.Registry.Count ?? 0;
        Row("emitter sites with at least one intake stem (of " + sitesTotal.ToString(ci) + ")", siteOfStem.Count.ToString(ci));
        Row("intake stems by nearest structure: emitter site / Wall Engine / Weather Lung", $"{siteEnds} / {engineEnds} / {lungEnds}");
        Row("stems ending in the sea", sea.ToString(ci));

        // ------------------------------------------------------------------ lakes against the keep-out edge
        // Edge cells: the Intake cells with the option on, with it off the Barrier cells that touch land (the cells that become Intake). A lake stands over keep-out ground where its level is above the lowest sample of an edge cell next to it.
        DrainageCellKind[] kind = field.Kind.ToArray();
        float[] lowest = field.Lowest.ToArray();
        int[] lakeId = field.LakeId.ToArray(), down = field.Down.ToArray();
        DrainageLake[] lakes = field.Lakes.ToArray();
        bool wrap = field.Wrap;
        int NeighbourCell(int cell, int d)
        {
            int row = cell / width, col = cell - row * width;
            int r = row + DrainageDomain.Dy[d], c = col + DrainageDomain.Dx[d];
            if ((uint)r >= (uint)height) return -1;
            if ((uint)c >= (uint)width) { if (!wrap) return -1; c = c < 0 ? width - 1 : 0; }
            return r * width + c;
        }
        bool TouchesLand(int cell)
        {
            for (int d = 0; d < 8; d++) { int n = NeighbourCell(cell, d); if (n >= 0 && kind[n] is DrainageCellKind.Land or DrainageCellKind.StripOutlet) return true; }
            return false;
        }
        bool IsEdge(int cell) => intakesOn ? kind[cell] == DrainageCellKind.Intake : kind[cell] == DrainageCellKind.Barrier && TouchesLand(cell);
        var damming = new HashSet<int>();
        double worst = 0;
        var spillIntoIntake = new HashSet<int>();
        for (int cell = 0; cell < kind.Length; cell++)
        {
            int lake = lakeId[cell];
            if (lake < 0) continue;
            if (down[cell] >= 0 && kind[down[cell]] == DrainageCellKind.Intake) spillIntoIntake.Add(lake);
            for (int d = 0; d < 8; d++)
            {
                int n = NeighbourCell(cell, d);
                if (n < 0 || !IsEdge(n)) continue;
                double diff = lakes[lake].Level - lowest[n];
                if (diff > 0) { damming.Add(lake); worst = Math.Max(worst, diff); }
            }
        }
        var dammingWithRiverLakes = new HashSet<int>();
        foreach (RiverStem st in net.Stems) if (st.Terminal == RiverTerminal.Lake && damming.Contains(st.TerminalId)) dammingWithRiverLakes.Add(st.TerminalId);
        Row("lakes above keep-out ground (level over the lowest sample of an edge cell beside the lake)", damming.Count.ToString(ci));
        Row("of those, lakes that a mapped river ends in", dammingWithRiverLakes.Count.ToString(ci));
        Row("largest level over edge ground (m)", F(worst));
        Row("lakes that spill straight into an intake cell", spillIntoIntake.Count.ToString(ci));
        Row("lakes", rep.LakeCount.ToString(ci));
        Row("flooded share of land (water cells)", F(100d * rep.LakeShareOfLand, "F2") + " %");
        Row("channel km at 100 km2 / at 50 km2", $"{F(rep.ChannelKm100, "F0")} / {F(rep.ChannelKm50, "F0")}");
        Row("river length (km)", F(rivers.LengthKm, "F0"));
        Row("mass balance relative error", rep.MassError.ToString("E2", ci));

        // ------------------------------------------------------------------ where the runoff goes
        double total = Math.Max(1e-12, rep.TotalRunoff), toSea = 0, toIntake = 0, toLung = 0, toStrip = 0;
        double[] acc = field.RunoffAccumulation.ToArray();
        for (int cell = 0; cell < kind.Length; cell++)
            switch (kind[cell])
            {
                case DrainageCellKind.Ocean: toSea += acc[cell]; break;
                case DrainageCellKind.Intake: toIntake += acc[cell]; break;
                case DrainageCellKind.Lung: toLung += acc[cell]; break;
                case DrainageCellKind.StripOutlet: toStrip += acc[cell]; break;
            }
        Row("runoff share through intakes", F(100d * toIntake / total, "F2") + " %");
        Row("runoff share to the sea", F(100d * toSea / total, "F2") + " %");
        Row("runoff share evaporated in closed basins", F(100d * rep.EvaporatedRunoff / total, "F2") + " %");
        Row("runoff share to the Weather Lung / strip ends", $"{F(100d * toLung / total, "F2")} % / {F(100d * toStrip / total, "F2")} %");

        // ------------------------------------------------------------------ centrelines against the sites (R10, R11)
        long samples = 0, within300 = 0, withinKeep = 0, inBarrierCell = 0, within300Body = 0, inCellBody = 0;
        var badStems = new HashSet<int>(); var badStemsBody = new HashSet<int>();
        double minClearance = double.PositiveInfinity; int minStem = -1;
        for (int i = 0; i < net.Stems.Length; i++)
            for (double s = 0; s < net.Curves[i].Length; s += 100)
            {
                net.Centre(i, s, out double around, out double across, out _, out _);
                double wrapped = net.WrapAround(around);
                samples++;
                (int cls, _, double clearance) = Nearest(wrapped, across);
                if (cls >= 0 && clearance < minClearance) { minClearance = clearance; minStem = i; }
                bool endTail = net.Stems[i].Terminal == RiverTerminal.Intake && s > net.Curves[i].Length - 1500d; // the last 1,500 m of an intake stem: the end node is not placed by this order (RS2 stem end)
                if (cls >= 0 && clearance < 300d) { within300++; badStems.Add(i); if (!endTail) { within300Body++; badStemsBody.Add(i); } }
                if (cls >= 0 && clearance < keepOut) withinKeep++;
                int col = (int)Math.Floor(wrapped / spec.AroundStepMeters) - spec.ColumnStart, row = (int)Math.Floor(across / spec.AcrossStepMeters);
                if (col < 0) col += spec.RingColumns;
                if (col >= 0 && col < width && row >= 0 && row < height && kind[row * width + col] is DrainageCellKind.Barrier or DrainageCellKind.Intake) { inBarrierCell++; if (!endTail) inCellBody++; }
            }
        Row("centreline samples (every 100 m)", samples.ToString(ci));
        Row("samples closer than influence radius + 300 m to a structure (must be 0)", within300.ToString(ci));
        Row("... the same, leaving out the last 1,500 m of intake stems (samples / stems)", $"{within300Body} / {badStemsBody.Count}");
        Row("... all samples closer than influence radius + 300 m: stems", badStems.Count.ToString(ci));
        Row("samples in a Barrier or Intake cell, leaving out the last 1,500 m of intake stems", inCellBody.ToString(ci));
        Row("samples closer than influence radius + keep-out to a structure", withinKeep.ToString(ci));
        Row("samples in a Barrier or Intake cell", inBarrierCell.ToString(ci));
        Row("smallest clearance of a centreline sample to a structure's influence boundary (m)", $"{F(minClearance, "F0")} (stem {minStem})");
        return sb.ToString();
    }
}
