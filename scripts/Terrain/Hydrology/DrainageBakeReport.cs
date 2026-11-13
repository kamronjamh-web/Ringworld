using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The bake report (docs/work-orders/terrain-vegetation/T2.md, T2R-4): seconds per stage, cell classes, lakes, cut depths, channel length, enclosed
/// land and hashes. Printed as text and JSON by the <c>drainage_bake_report</c> tool. Numbers that decide something are printed here, not derived elsewhere.
/// </summary>
public sealed record DrainageBakeReport
{
    public int Columns { get; init; }
    public int Rows { get; init; }
    public bool WholeRing { get; init; }
    public double CellAreaKm2 { get; init; }
    public double LakeAbundance { get; init; }

    public double GridSeconds { get; init; }
    public double BarrierSeconds { get; init; }
    public double NoiseSeconds { get; init; }
    public double ConditioningSeconds { get; init; }
    public double FloodSeconds { get; init; }
    public double BreachSearchSeconds { get; init; }
    public double LakeSeconds { get; init; }
    public double RoutingSeconds { get; init; }
    public double RouteDirectionsSeconds { get; init; }
    public double RouteRepairSeconds { get; init; }
    public double RouteAccumulationSeconds { get; init; }
    public double BakeSeconds => BarrierSeconds + NoiseSeconds + ConditioningSeconds + RoutingSeconds;

    public int LandCells { get; init; }
    public int SeaCells { get; init; }
    public int BarrierCells { get; init; }
    public int LungCells { get; init; }
    /// <summary>T2R-19: Intake cells (zero, and left out of the JSON, unless the option is on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int IntakeCells { get; init; }
    /// <summary>Land cells that are not barriers: the denominator of the lake share.</summary>
    public int NonBarrierLandCells { get; init; }

    public int LakeCount { get; init; }
    public int LakeWaterCells { get; init; }
    public int LakeFloodedCells { get; init; }
    public double LakeShareOfLand { get; init; }
    public double LakeDepthP50 { get; init; }
    public double LakeDepthMax { get; init; }
    public int RoutingFillBasins { get; init; }
    public int RoutingFillCells { get; init; }

    // S8b: water balance.
    public double EvaporationE0 { get; init; }
    public double BalanceLambda { get; init; }
    public int BasinsClosed { get; init; }
    public int BasinsSpilling { get; init; }
    public int LakesClosed { get; init; }
    public int LakesSpilling { get; init; }
    public int DryPans { get; init; }
    public int MultiPatchLakes { get; init; }
    public int MultiRootSpills { get; init; }
    public int PitsAboveSpill { get; init; }
    public int BasinCrossings { get; init; }
    public int DryCells { get; init; }
    public double DryShareOfLand { get; init; }
    public double ClosedDepthP50 { get; init; }
    public double ClosedDepthP90 { get; init; }
    public double ClosedDepthP99 { get; init; }
    public double ClosedDepthMax { get; init; }
    public double SpillingDepthP50 { get; init; }
    public double SpillingDepthP90 { get; init; }
    public double SpillingDepthP99 { get; init; }
    public double SpillingDepthMax { get; init; }
    public double LakeDepthP90 { get; init; }
    public double LakeDepthP99 { get; init; }
    public string DeepestLakes { get; init; } = "";
    public string LargestDryFloors { get; init; } = "";
    public double TotalRunoff { get; init; }
    public double OutflowRunoff { get; init; }
    public double EvaporatedRunoff { get; init; }
    public double MassError { get; init; }
    public double BalanceSeconds { get; init; }

    public int BreachCandidates { get; init; }
    public int BreachFeasible { get; init; }
    public int BreachApplied { get; init; }
    public long BreachSearchStates { get; init; }
    public double CutDepthP50 { get; init; }
    public double CutDepthP90 { get; init; }
    public double CutDepthMax { get; init; }
    public double CutBreachMaxP50 { get; init; }
    public double CutBreachMaxP90 { get; init; }
    public double CutCellMeters3Km3 { get; init; }

    public double ChannelKm50 { get; init; }
    public double ChannelKm100 { get; init; }
    public int FlatCrossingsRepaired { get; init; }
    public int FlatCrossingsUnresolved { get; init; }
    public int DescendingCrossings { get; init; }
    public int EnclosedLandCells { get; init; }
    public double EnclosedLandShare { get; init; }
    public bool Conserved { get; init; }

    public string GridHash { get; init; } = "";
    public string FilledHash { get; init; } = "";
    public string LakeHash { get; init; } = "";
    public string DownHash { get; init; } = "";

    internal static DrainageBakeReport Build(DrainageDomain d, DrainageConditioned c, DrainageRouted r, double lakeAbundance, ulong gridHash,
        double barrierSeconds, double noiseSeconds, double conditioningSeconds, double routingSeconds, double evaporationScale = 1d)
    {
        DrainageBalanceParameters balance = DrainageBalanceParameters.From(lakeAbundance, evaporationScale);
        double parametersE0 = balance.E0, parametersLambda = balance.Lambda;
        int land = 0, sea = 0, barrier = 0, lung = 0, intake = 0;
        for (int i = 0; i < d.Count; i++)
            switch (d.Kind[i])
            {
                case DrainageCellKind.Land or DrainageCellKind.StripOutlet: land++; break;
                case DrainageCellKind.Ocean: sea++; break;
                case DrainageCellKind.Barrier: barrier++; break;
                case DrainageCellKind.Lung: lung++; break;
                case DrainageCellKind.Intake: intake++; break;
            }
        int water = 0, flooded = 0;
        for (int i = 0; i < d.Count; i++)
        {
            int lake = r.LakeId[i];
            if (lake < 0) continue;
            flooded++;
            if (c.Effective[i] < r.Lakes[lake].Level) water++;
        }
        var lakeDepths = new List<double>(r.Lakes.Length);
        foreach (DrainageLake lake in r.Lakes) lakeDepths.Add(lake.DepthMeters);
        var closedDepths = new List<double>();
        var spillingDepths = new List<double>();
        foreach (DrainageLake lake in r.Lakes) (lake.Closed ? closedDepths : spillingDepths).Add(lake.DepthMeters);
        int basinsClosed = 0, dryCells = 0;
        foreach (DrainageBasinInfo basin in r.Basins) { if (basin.Closed) basinsClosed++; dryCells += basin.DryCells; }
        var deepest = new StringBuilder();
        var byDepth = new List<DrainageLake>(r.Lakes);
        byDepth.Sort(static (a, b) => b.DepthMeters.CompareTo(a.DepthMeters));
        for (int i = 0; i < Math.Min(5, byDepth.Count); i++)
            deepest.Append(FormattableString.Invariant($"{(i > 0 ? "; " : "")}lake {byDepth[i].Id} depth {byDepth[i].DepthMeters:F0} m level {byDepth[i].Level:F0} m {(byDepth[i].Closed ? "closed" : "spilling")} {byDepth[i].FloodedCells} cells"));
        var largestDry = new StringBuilder();
        var dryOrder = new List<int>();
        for (int k = 0; k < r.Basins.Length; k++) if (r.Basins[k].DryCells > 0) dryOrder.Add(k);
        dryOrder.Sort((a, b) => r.Basins[b].DryCells.CompareTo(r.Basins[a].DryCells));
        for (int i = 0; i < Math.Min(5, dryOrder.Count); i++)
        {
            int cell0 = c.Lakes[dryOrder[i]].PitCell;
            largestDry.Append(FormattableString.Invariant($"{(i > 0 ? "; " : "")}basin {dryOrder[i]} dry {r.Basins[dryOrder[i]].DryCells} km^2 of {r.Basins[dryOrder[i]].Cells} cells at col {cell0 % d.Width} row {cell0 / d.Width}, lakes {r.Basins[dryOrder[i]].Lakes}, inflow {r.Basins[dryOrder[i]].Inflow:F0}"));
        }

        var cellCuts = new List<double>();
        var breachMax = new List<double>(c.Cuts.Length);
        double volume = 0;
        foreach (DrainageBreach cut in c.Cuts)
        {
            float datum = d.Route[cut.PitCell];
            for (int i = 0; i < cut.Path.Length - 1; i++)
            {
                double depth = d.Route[cut.Path[i]] - datum;
                if (depth <= 0) continue;
                cellCuts.Add(depth);
                volume += depth * d.CellAreaKm2 / 1000d;
            }
            breachMax.Add(cut.MaxDepthMeters);
        }

        double channel50 = ChannelKm(d, c, r, 50d), channel100 = ChannelKm(d, c, r, 100d);
        int nonBarrierLand = land;
        return new DrainageBakeReport
        {
            Columns = d.Width, Rows = d.Height, WholeRing = d.Wrap, CellAreaKm2 = d.CellAreaKm2, LakeAbundance = lakeAbundance,
            BarrierSeconds = barrierSeconds, NoiseSeconds = noiseSeconds, ConditioningSeconds = conditioningSeconds, FloodSeconds = c.FloodSeconds,
            BreachSearchSeconds = c.SearchSeconds, LakeSeconds = c.LakeSeconds, RoutingSeconds = routingSeconds,
            RouteDirectionsSeconds = r.DirectionsSeconds, RouteRepairSeconds = r.RepairSeconds, RouteAccumulationSeconds = r.AccumulationSeconds,
            LandCells = land, SeaCells = sea, BarrierCells = barrier, LungCells = lung, IntakeCells = intake, NonBarrierLandCells = nonBarrierLand,
            LakeCount = r.Lakes.Length, LakeWaterCells = water, LakeFloodedCells = flooded,
            LakeShareOfLand = nonBarrierLand == 0 ? 0 : (double)water / nonBarrierLand,
            LakeDepthP50 = Percentile(lakeDepths, 0.5), LakeDepthMax = Percentile(lakeDepths, 1),
            RoutingFillBasins = c.RoutingFillBasins, RoutingFillCells = c.RoutingFillCells,
            EvaporationE0 = parametersE0, BalanceLambda = parametersLambda,
            BasinsClosed = basinsClosed, BasinsSpilling = r.Basins.Length - basinsClosed, LakesClosed = closedDepths.Count, LakesSpilling = spillingDepths.Count,
            DryPans = r.DryPans, MultiPatchLakes = r.MultiPatchLakes, MultiRootSpills = r.MultiRootSpills, PitsAboveSpill = r.PitsAboveSpill, BasinCrossings = r.BasinCrossings,
            DryCells = dryCells, DryShareOfLand = nonBarrierLand == 0 ? 0 : (double)dryCells / nonBarrierLand,
            ClosedDepthP50 = Percentile(closedDepths, 0.5), ClosedDepthP90 = Percentile(closedDepths, 0.9), ClosedDepthP99 = Percentile(closedDepths, 0.99), ClosedDepthMax = Percentile(closedDepths, 1),
            SpillingDepthP50 = Percentile(spillingDepths, 0.5), SpillingDepthP90 = Percentile(spillingDepths, 0.9), SpillingDepthP99 = Percentile(spillingDepths, 0.99), SpillingDepthMax = Percentile(spillingDepths, 1),
            LakeDepthP90 = Percentile(lakeDepths, 0.9), LakeDepthP99 = Percentile(lakeDepths, 0.99),
            DeepestLakes = deepest.ToString(), LargestDryFloors = largestDry.ToString(),
            TotalRunoff = r.TotalRunoff, OutflowRunoff = r.OutflowRunoff, EvaporatedRunoff = r.EvaporatedRunoff, MassError = r.MassError, BalanceSeconds = r.BalanceSeconds,
            BreachCandidates = c.Candidates, BreachFeasible = c.Feasible, BreachApplied = c.Cuts.Length, BreachSearchStates = c.SearchStates,
            CutDepthP50 = Percentile(cellCuts, 0.5), CutDepthP90 = Percentile(cellCuts, 0.9), CutDepthMax = Percentile(cellCuts, 1),
            CutBreachMaxP50 = Percentile(breachMax, 0.5), CutBreachMaxP90 = Percentile(breachMax, 0.9), CutCellMeters3Km3 = volume,
            ChannelKm50 = channel50, ChannelKm100 = channel100,
            FlatCrossingsRepaired = r.FlatCrossingsRepaired, FlatCrossingsUnresolved = r.FlatCrossingsUnresolved, DescendingCrossings = r.DescendingCrossings,
            EnclosedLandCells = r.EnclosedLandCells, EnclosedLandShare = land == 0 ? 0 : (double)r.EnclosedLandCells / land, Conserved = r.Conserved,
            GridHash = Hex(gridHash), FilledHash = Hex(HashFilled(c)), LakeHash = Hex(HashLakes(r)), DownHash = Hex(r.DownHash),
        };
    }

    private static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>Channel length (km) of land cells with at least <paramref name="thresholdKm2"/> contributing area that are not lake water.</summary>
    internal static double ChannelKm(DrainageDomain d, DrainageConditioned c, DrainageRouted r, double thresholdKm2)
    {
        int minCells = (int)Math.Ceiling(thresholdKm2 / d.CellAreaKm2);
        double meters = 0;
        for (int cell = 0; cell < d.Count; cell++)
        {
            if (r.AreaCells[cell] < minCells || !d.IsLand(cell) || d.IsOutlet(cell)) continue;
            int next = r.Down[cell];
            if (next < 0) continue;
            int lake = r.LakeId[cell];
            if (lake >= 0 && c.Effective[cell] < r.Lakes[lake].Level) continue;
            int direction = d.Direction(cell, next);
            meters += direction >= 0 ? d.StepMeters(direction) : d.AroundStepMeters;
        }
        return meters / 1000d;
    }

    private static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[(int)Math.Min(values.Count - 1, Math.Floor(q * (values.Count - 1) + 0.5))];
    }

    private static ulong HashFilled(DrainageConditioned c)
    {
        ulong h = 14695981039346656037UL;
        foreach (float v in c.Filled) h = (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;
        foreach (int v in c.FloodOrder) h = (h ^ (uint)v) * 1099511628211UL;
        return h;
    }

    private static ulong HashLakes(DrainageRouted c)
    {
        ulong h = 14695981039346656037UL;
        foreach (DrainageLake lake in c.Lakes)
        {
            h = (h ^ (uint)lake.PitCell) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(lake.Level)) * 1099511628211UL;
            h = (h ^ (uint)lake.WaterCells) * 1099511628211UL;
        }
        foreach (int id in c.LakeId) h = (h ^ (uint)id) * 1099511628211UL;
        return h;
    }

    public string ToText()
    {
        var s = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        s.AppendLine(FormattableString.Invariant($"grid {Columns} x {Rows} cells ({(WholeRing ? "whole ring" : "strip")}), cell {CellAreaKm2:F3} km^2, lake_abundance {LakeAbundance:F2}"));
        s.AppendLine(FormattableString.Invariant($"seconds: grid {GridSeconds:F2}  barriers {BarrierSeconds:F2}  noise {NoiseSeconds:F2}  conditioning {ConditioningSeconds:F2} (flood {FloodSeconds:F2}, breach search {BreachSearchSeconds:F2}, lakes {LakeSeconds:F2})  routing {RoutingSeconds:F2} (directions {RouteDirectionsSeconds:F2}, repair {RouteRepairSeconds:F2}, accumulation {RouteAccumulationSeconds:F2})  bake total (without grid) {BakeSeconds:F2}  with grid {BakeSeconds + GridSeconds:F2}"));
        s.AppendLine(FormattableString.Invariant($"cells: land {LandCells}  sea {SeaCells}  barrier {BarrierCells}  lung {LungCells}"));
        if (IntakeCells > 0) s.AppendLine(FormattableString.Invariant($"site intakes (T2R-19): {IntakeCells} intake cells (outlets at the edge of the keep-out)"));
        s.AppendLine(FormattableString.Invariant($"lakes: {LakeCount}, water cells {LakeWaterCells} ({LakeShareOfLand:P2} of non-barrier land), flooded cells {LakeFloodedCells}, depth p50 {LakeDepthP50:F1} m max {LakeDepthMax:F1} m; routing fill {RoutingFillBasins} basins / {RoutingFillCells} cells"));
        s.AppendLine(FormattableString.Invariant($"water balance: E0 {EvaporationE0:F1}, lambda {BalanceLambda:F3}; basins {BasinsClosed + BasinsSpilling} (closed {BasinsClosed}, spilling {BasinsSpilling}); lakes closed {LakesClosed}, spilling {LakesSpilling}; dry pans {DryPans}, multi-patch lakes {MultiPatchLakes}, multi-root spills {MultiRootSpills}, pits above spill {PitsAboveSpill}, basin crossings (legal) {BasinCrossings}; dry floor {DryCells} cells ({DryShareOfLand:P2} of land); balance {BalanceSeconds:F2} s"));
        s.AppendLine(FormattableString.Invariant($"lake depth (level minus lowest cell) all p50/p90/p99/max {LakeDepthP50:F1}/{LakeDepthP90:F1}/{LakeDepthP99:F1}/{LakeDepthMax:F1} m; closed {ClosedDepthP50:F1}/{ClosedDepthP90:F1}/{ClosedDepthP99:F1}/{ClosedDepthMax:F1} m; spilling {SpillingDepthP50:F1}/{SpillingDepthP90:F1}/{SpillingDepthP99:F1}/{SpillingDepthMax:F1} m"));
        s.AppendLine("deepest lakes: " + DeepestLakes);
        s.AppendLine("largest dry floors: " + LargestDryFloors);
        s.AppendLine(FormattableString.Invariant($"mass balance: runoff over land {TotalRunoff:F2}, to outlets {OutflowRunoff:F2}, evaporated in basins {EvaporatedRunoff:F2}, relative error {MassError:E2}"));
        s.AppendLine(FormattableString.Invariant($"breaches: candidates {BreachCandidates}, feasible {BreachFeasible}, applied {BreachApplied}, search states {BreachSearchStates}; cut depth per cell p50 {CutDepthP50:F1} m p90 {CutDepthP90:F1} m max {CutDepthMax:F1} m; per breach max p50 {CutBreachMaxP50:F1} p90 {CutBreachMaxP90:F1}; excavation {CutCellMeters3Km3:F2} km^3"));
        s.AppendLine(FormattableString.Invariant($"channels: {ChannelKm50:F0} km at 50 km^2, {ChannelKm100:F0} km at 100 km^2; flat crossings repaired {FlatCrossingsRepaired}, unresolved {FlatCrossingsUnresolved}, descending crossings (legal) {DescendingCrossings}"));
        s.AppendLine(FormattableString.Invariant($"enclosed land {EnclosedLandCells} cells ({EnclosedLandShare:P3}); area conserved {Conserved}"));
        s.AppendLine($"hashes: grid {GridHash} filled {FilledHash} lakes {LakeHash} down {DownHash}");
        _ = ci;
        return s.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
