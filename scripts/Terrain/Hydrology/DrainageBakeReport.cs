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
        double barrierSeconds, double noiseSeconds, double conditioningSeconds, double routingSeconds)
    {
        int land = 0, sea = 0, barrier = 0, lung = 0;
        for (int i = 0; i < d.Count; i++)
            switch (d.Kind[i])
            {
                case DrainageCellKind.Land or DrainageCellKind.StripOutlet: land++; break;
                case DrainageCellKind.Ocean: sea++; break;
                case DrainageCellKind.Barrier: barrier++; break;
                case DrainageCellKind.Lung: lung++; break;
            }
        int water = 0, flooded = 0;
        for (int i = 0; i < d.Count; i++)
        {
            int lake = c.LakeId[i];
            if (lake < 0) continue;
            flooded++;
            if (c.Effective[i] < c.Lakes[lake].Level) water++;
        }
        var lakeDepths = new List<double>(c.Lakes.Length);
        foreach (DrainageLake lake in c.Lakes) lakeDepths.Add(lake.DepthMeters);

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
            LandCells = land, SeaCells = sea, BarrierCells = barrier, LungCells = lung, NonBarrierLandCells = nonBarrierLand,
            LakeCount = c.Lakes.Length, LakeWaterCells = water, LakeFloodedCells = flooded,
            LakeShareOfLand = nonBarrierLand == 0 ? 0 : (double)water / nonBarrierLand,
            LakeDepthP50 = Percentile(lakeDepths, 0.5), LakeDepthMax = Percentile(lakeDepths, 1),
            RoutingFillBasins = c.RoutingFillBasins, RoutingFillCells = c.RoutingFillCells,
            BreachCandidates = c.Candidates, BreachFeasible = c.Feasible, BreachApplied = c.Cuts.Length, BreachSearchStates = c.SearchStates,
            CutDepthP50 = Percentile(cellCuts, 0.5), CutDepthP90 = Percentile(cellCuts, 0.9), CutDepthMax = Percentile(cellCuts, 1),
            CutBreachMaxP50 = Percentile(breachMax, 0.5), CutBreachMaxP90 = Percentile(breachMax, 0.9), CutCellMeters3Km3 = volume,
            ChannelKm50 = channel50, ChannelKm100 = channel100,
            FlatCrossingsRepaired = r.FlatCrossingsRepaired, FlatCrossingsUnresolved = r.FlatCrossingsUnresolved, DescendingCrossings = r.DescendingCrossings,
            EnclosedLandCells = r.EnclosedLandCells, EnclosedLandShare = land == 0 ? 0 : (double)r.EnclosedLandCells / land, Conserved = r.Conserved,
            GridHash = Hex(gridHash), FilledHash = Hex(HashFilled(c)), LakeHash = Hex(HashLakes(c)), DownHash = Hex(r.DownHash),
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
            int lake = c.LakeId[cell];
            if (lake >= 0 && c.Effective[cell] < c.Lakes[lake].Level) continue;
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

    private static ulong HashLakes(DrainageConditioned c)
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
        s.AppendLine(FormattableString.Invariant($"lakes: {LakeCount}, water cells {LakeWaterCells} ({LakeShareOfLand:P2} of non-barrier land), flooded cells {LakeFloodedCells}, depth p50 {LakeDepthP50:F1} m max {LakeDepthMax:F1} m; routing fill {RoutingFillBasins} basins / {RoutingFillCells} cells"));
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
