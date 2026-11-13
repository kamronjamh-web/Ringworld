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
/// T2R-23 Part A (docs/work-orders/terrain-vegetation/T2-rules.md, Spec RS2 "Measurement table"): the <c>--site-removal=&lt;branch|catchment&gt;</c> measurement. The bake runs with the site intakes on (edge cells of the keep-out are outlets, a basin that spills into
/// one holds no lake) and the builder with the rule. A second build of the same field without the rule gives the rivers that the rule took: a stem of that build is removed when no stem of the rule build has the same head and last cell. Reads only; the graph is not changed.
/// Every line is "key | value" so the runs can be tabulated.
/// </summary>
public partial class DrainageBakeReportTool
{
    /// <summary>Nearest structure at a point: 0 emitter site, 1 Wall Engine stadium, 2 Weather Lung; the clearance is the distance to its influence boundary.</summary>
    private sealed class SiteGeometry
    {
        private readonly EmitterSiteTerrainShaper? _shaper;
        private readonly ClimateMachine[] _machines;
        private readonly double _machineHalf, _machineInfluence, _ring, _lungHx, _lungHy;
        private readonly bool _hasLung;
        private readonly long _lungAround;
        private readonly int _lungAcross;

        public SiteGeometry(TerrainGenerationParameters recipe, double ringMeters)
        {
            _ring = ringMeters;
            _shaper = recipe.HasEmitterSites ? EmitterSiteTerrainShaper.GetOrCreate(recipe) : null;
            _machines = _shaper?.Climate != null ? _shaper.Climate.Machines.ToArray() : Array.Empty<ClimateMachine>();
            _machineHalf = _shaper?.Climate != null ? _shaper.Climate.Recipe.ForecourtLengthM * 0.5 : 0;
            _machineInfluence = _shaper?.Climate != null ? _shaper.Climate.Recipe.InfluenceRadiusM : 0;
            _hasLung = _shaper?.WeatherLung != null;
            if (_hasLung) { _lungHx = _shaper!.WeatherLung!.Recipe.BoundHalfXM; _lungHy = _shaper.WeatherLung.Recipe.BoundHalfYM; _lungAround = _shaper.WeatherLung.Site.Around; _lungAcross = _shaper.WeatherLung.Site.Across; }
        }

        public int EmitterSites => _shaper?.Registry.Count ?? 0;
        public int WallEngines => _machines.Length;

        public (int Class, int Index, double Clearance) Nearest(double around, double across)
        {
            int cls = -1, index = -1;
            double best = double.PositiveInfinity;
            if (_shaper != null && _shaper.Registry.TryGetNearestSite((long)Math.Floor(around), across, out int site, out double du, out double dw))
            { best = Math.Sqrt(du * du + dw * dw) - _shaper.Registry[site].InfluenceRadius; cls = 0; index = site; }
            for (int i = 0; i < _machines.Length; i++)
            {
                double da = Math.Abs(around - _machines[i].Around); da = Math.Min(da, _ring - da);
                double sx = Math.Max(0d, da - _machineHalf), sy = across - _machines[i].Across;
                double c = Math.Sqrt(sx * sx + sy * sy) - _machineInfluence;
                if (c < best) { best = c; cls = 1; index = i; }
            }
            if (_hasLung)
            {
                double da = Math.Abs(around - _lungAround); da = Math.Min(da, _ring - da);
                double ex = Math.Max(0d, da - _lungHx), ey = Math.Max(0d, Math.Abs(across - _lungAcross) - _lungHy);
                double c = Math.Sqrt(ex * ex + ey * ey);
                if (c < best) { best = c; cls = 2; index = 0; }
            }
            return (cls, index, best);
        }
    }

    private static string SiteRemovalReport(RiverSiteRemovalRule rule, RiverNetwork net, RiverNetwork reference, RiverBuildReport rivers, RiverBuildReport referenceRivers, DrainageField field, DrainageGrid grid, TerrainGenerationParameters recipe)
    {
        var sb = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        DrainageGridSpec spec = grid.Spec;
        DrainageBakeReport rep = field.Report;
        DrainageDomain domain = field.Domain;
        int width = field.Width, height = field.Height;
        var geometry = new SiteGeometry(recipe, spec.CircumferenceMeters);
        void Row(string key, string value) => sb.AppendLine(key + " | " + value);
        string F(double v, string fmt = "F1") => v.ToString(fmt, ci);
        string Q3(List<double> v, string fmt = "F2") { v.Sort(); return $"{F(Quantile(v, 0.5), fmt)} / {F(Quantile(v, 0.9), fmt)} / {F(v.Count == 0 ? double.NaN : v[^1], fmt)}"; }
        int[] down = field.Down.ToArray(), lakeId = field.LakeId.ToArray();
        DrainageCellKind[] kind = field.Kind.ToArray(), terminal = field.Terminal.ToArray();
        DrainageLake[] lakes = field.Lakes.ToArray();
        double perKm2 = RiverHydraulics.DischargePerKm2;
        double total = Math.Max(1e-12, rep.TotalRunoff);

        Row("site removal rule", rule.ToString());
        Row("keep-out cells (barrier + intake), of them edge cells (intake)", $"{rep.BarrierCells + rep.IntakeCells} / {rep.IntakeCells}");
        Row("site-drained basins: count / cells / inflow passed on (m3/s) / share of runoff", $"{rep.SiteDrainedBasins} / {rep.SiteDrainedCells} / {F(rep.SiteDrainedInflow * perKm2)} / {F(100d * rep.SiteDrainedInflow / total, "F2")} %");
        Row("runoff that arrives at edge cells, share of the ring's runoff", F(100d * rep.SiteRunoff / total, "F2") + " %");

        // ------------------------------------------------------------------ lakes beside an edge cell that stay
        var besideEdge = new HashSet<int>();
        double nearest = double.PositiveInfinity;
        for (int cell = 0; cell < kind.Length; cell++)
        {
            int lake = lakeId[cell];
            if (lake < 0 || !field.IsLakeWater(cell)) continue;
            bool beside = false;
            for (int d = 0; d < 8 && !beside; d++) { int n = domain.GeometricNeighbor(cell, d); beside = n >= 0 && kind[n] == DrainageCellKind.Intake; }
            if (!beside) continue;
            besideEdge.Add(lake);
            (long a, int c) = spec.Centre(cell % width, cell / width);
            (_, _, double clearance) = geometry.Nearest(((double)a % spec.CircumferenceMeters + spec.CircumferenceMeters) % spec.CircumferenceMeters, c);
            nearest = Math.Min(nearest, clearance);
        }
        Row("lakes with a wet cell beside an edge cell (they stay): count / smallest distance of such a wet cell to a site shape (m)", $"{besideEdge.Count} / {(double.IsInfinity(nearest) ? "-" : F(nearest, "F0"))}");

        // ------------------------------------------------------------------ the rivers the rule took (the reference build has the same field and no rule)
        var kept = new HashSet<(int Head, int Last)>();
        foreach (RiverStem st in net.Stems) kept.Add((st.HeadCell, st.LastCell));
        int[] areaCells = field.AreaCells.ToArray();
        bool[]? siteDrainedCell = RiverSiteRemoval.SiteDrainedCells(field);
        var removed = new List<int>();
        foreach (int i in Enumerable.Range(0, reference.Stems.Length)) if (!kept.Contains((reference.Stems[i].HeadCell, reference.Stems[i].LastCell))) removed.Add(i);
        double referenceKm = reference.Curves.Sum(c => c.Length) / 1000d;
        var byReasonCount = new int[4]; var byReasonKm = new double[4];
        var removedQ = new List<double>(); var removedW = new List<double>();
        double removedKm = 0, removedQSum = 0, allEndQ = 0;
        var siteSet = new HashSet<int>(); var engineSet = new HashSet<int>(); bool lungHit = false;
        foreach (RiverStem st in reference.Stems) if (st.Terminal != RiverTerminal.Confluence) allEndQ += reference.Reaches[st.ReachStart + st.ReachCount - 1].Q;
        foreach (int i in removed)
        {
            RiverStem st = reference.Stems[i];
            byte reason = RiverSiteRemoval.Classify(st.LastCell, down, kind, terminal, siteDrainedCell);
            double km = reference.Curves[i].Length / 1000d;
            byReasonCount[reason]++; byReasonKm[reason] += km; removedKm += km;
            RiverReach last = reference.Reaches[st.ReachStart + st.ReachCount - 1];
            removedQ.Add(last.Q); removedQSum += last.Q; removedW.Add(reference.Sections[st.ReachStart + st.ReachCount - 1].Width);
            if (reason == RiverSiteRemoval.ReasonDrainsToSite) continue; // the site of a tributary is the site of the river it joined
            int edge = st.LastCell;
            while (edge >= 0 && kind[edge] != DrainageCellKind.Intake) edge = down[edge];
            if (edge < 0) continue;
            (long a, int c) = spec.Centre(edge % width, edge / width);
            (int cls, int index, _) = geometry.Nearest(((double)a % spec.CircumferenceMeters + spec.CircumferenceMeters) % spec.CircumferenceMeters, c);
            if (cls == 0) siteSet.Add(index); else if (cls == 1) engineSet.Add(index); else if (cls == 2) lungHit = true;
        }
        Row("rivers without the rule: stems / km", $"{reference.Stems.Length} / {F(referenceKm, "F0")}");
        Row("rivers left: stems / km", $"{net.Stems.Length} / {F(net.Curves.Sum(c => c.Length) / 1000d, "F0")}");
        Row("removed rivers: stems / km / share of the river length", $"{removed.Count} / {F(removedKm, "F0")} / {F(100d * removedKm / Math.Max(1e-9, referenceKm), "F2")} %");
        Row("removed by reason: ends at a site / inlet of a site-drained basin / drains to a site (rule C only): stems", $"{byReasonCount[1]} / {byReasonCount[2]} / {byReasonCount[3]}");
        Row("removed by reason: km", $"{F(byReasonKm[1], "F0")} / {F(byReasonKm[2], "F0")} / {F(byReasonKm[3], "F0")}");
        Row("rule C beyond rule L: stems / km (the stems of reason 3: tributaries of removed rivers, all that drain to a site)", $"{byReasonCount[3]} / {F(byReasonKm[3], "F0")}");
        removedQ.Sort(); removedW.Sort();
        Row("removed discharge at the end (m3/s) p50 / p90 / sum", $"{F(Quantile(removedQ, 0.5), "F2")} / {F(Quantile(removedQ, 0.9), "F2")} / {F(removedQSum, "F1")}");
        Row("removed width at the end (m, law of RS3) p50 / p90 / max", Q3(removedW, "F1"));
        Row("removed discharge as a share of the discharge of all river ends of the reference build", F(100d * removedQSum / Math.Max(1e-9, allEndQ), "F2") + " %");
        Row("sites with at least one removed river: emitter sites (of " + geometry.EmitterSites + ") / Wall Engines (of " + geometry.WallEngines + ") / Weather Lung", $"{siteSet.Count} / {engineSet.Count} / {(lungHit ? 1 : 0)}");

        // ------------------------------------------------------------------ rule L: Cut ends and lakes that lost their outlet river
        var cutQ = new List<double>(); var cutW = new List<double>(); var cutDist = new List<double>();
        int cutStems = 0;
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStem st = net.Stems[i];
            if (st.Terminal != RiverTerminal.Cut) continue;
            cutStems++;
            int reach = st.ReachStart + st.ReachCount - 1;
            cutQ.Add(net.Reaches[reach].Q); cutW.Add(net.Sections[reach].Width);
            double dist = 0;
            int cur = st.LastCell;
            for (int guard = 0; guard < 100000 && cur >= 0 && kind[cur] != DrainageCellKind.Intake; guard++)
            {
                int next = down[cur];
                if (next < 0) break;
                int dir = domain.Direction(cur, next);
                dist += dir >= 0 ? domain.StepMeters(dir) : domain.AroundStepMeters;
                cur = next;
            }
            cutDist.Add(dist / 1000d);
        }
        Row("Cut ends (rule L): count", cutStems.ToString(ci));
        Row("Cut ends: discharge (m3/s) p50 / p90 / max", Q3(cutQ));
        Row("Cut ends: width (m) p50 / p90 / max", Q3(cutW, "F1"));
        Row("Cut ends: distance along the removed river from the Cut end to the edge cell (km) p50 / p90 / max", Q3(cutDist, "F1"));
        var lostLakes = new HashSet<int>();
        foreach (int i in removed)
        {
            RiverStem st = reference.Stems[i];
            if (st.Terminal == RiverTerminal.Lake || lakeId[st.HeadCell] < 0 || reference.Reaches[st.ReachStart].UpstreamLake != lakeId[st.HeadCell]) continue;
            lostLakes.Add(lakeId[st.HeadCell]);
        }
        var inletsOf = new Dictionary<int, int>();
        foreach (RiverStem st in net.Stems)
        {
            if (st.Terminal == RiverTerminal.Lake) inletsOf[st.TerminalId] = inletsOf.GetValueOrDefault(st.TerminalId) + 1;
        }
        var lostInflow = new List<double>(); int lostWithInlets = 0;
        foreach (int lake in lostLakes)
        {
            lostInflow.Add(lakes[lake].Inflow * perKm2);
            if (inletsOf.GetValueOrDefault(lake) > 0) lostWithInlets++;
        }
        Row("lakes that lost their outlet river: count / of those with an inlet river left (rule L)", $"{lostLakes.Count} / {lostWithInlets}");
        Row("lakes that lost their outlet river: inflow (m3/s) p50 / p90 / max", Q3(lostInflow, "F1"));

        // ------------------------------------------------------------------ river ends by class and the runoff shares
        var ends = new int[8];
        foreach (RiverStem st in net.Stems) ends[(int)st.Terminal]++;
        Row("river ends of the rivers left: sea / lake / lung / strip end / dead / cut / intake", $"{ends[(int)RiverTerminal.Sea]} / {ends[(int)RiverTerminal.Lake]} / {ends[(int)RiverTerminal.Lung]} / {ends[(int)RiverTerminal.StripEnd]} / {ends[(int)RiverTerminal.Dead]} / {ends[(int)RiverTerminal.Cut]} / {ends[(int)RiverTerminal.Intake]}");
        Row("river ends of the reference build: sea / lake / lung / strip end / dead / cut / intake", string.Join(" / ", new[] { RiverTerminal.Sea, RiverTerminal.Lake, RiverTerminal.Lung, RiverTerminal.StripEnd, RiverTerminal.Dead, RiverTerminal.Cut, RiverTerminal.Intake }.Select(t => reference.Stems.Count(s => s.Terminal == t).ToString(ci))));
        Row("builder: removed stems / cells, guard moved nodes / largest move (m), terminal cut", $"{rivers.RemovedStems} / {rivers.RemovedCells}, {rivers.GuardMovedNodes} / {F(rivers.GuardMaxMeters, "F0")}, {rivers.TerminalCut} (reference: guard moved {referenceRivers.GuardMovedNodes} / {F(referenceRivers.GuardMaxMeters, "F0")})");

        // ------------------------------------------------------------------ centrelines against the bar, by how the stem ends
        long within = 0, cutWithin = 0;
        var badStems = new Dictionary<int, (long Samples, double Min)>();
        for (int i = 0; i < net.Stems.Length; i++)
            for (double s = 0; s < net.Curves[i].Length; s += 100)
            {
                net.Centre(i, s, out double around, out double across, out _, out _);
                (int cls, _, double clearance) = geometry.Nearest(net.WrapAround(around), across);
                if (cls < 0 || clearance >= 300d) continue;
                within++;
                if (net.Stems[i].Terminal == RiverTerminal.Cut) cutWithin++;
                badStems[i] = badStems.TryGetValue(i, out var v) ? (v.Samples + 1, Math.Min(v.Min, clearance)) : (1, clearance);
            }
        Row("centreline samples closer than influence radius + 300 m (every 100 m): all / on Cut stems / stems", $"{within} / {cutWithin} / {badStems.Count}");
        foreach (var kv in badStems.OrderBy(k => k.Value.Min).Take(12))
        {
            RiverStem st = net.Stems[kv.Key];
            Row($"  stem {kv.Key} terminal {st.Terminal}, last cell {st.LastCell} (col {st.LastCell % width}, row {st.LastCell / width}), length {net.Curves[kv.Key].Length / 1000d:F1} km", $"{kv.Value.Samples} samples, smallest clearance {F(kv.Value.Min, "F0")} m");
        }
        return sb.ToString();
    }
}
