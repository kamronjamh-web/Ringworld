using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-6c and T2R-17 (docs/work-orders/terrain-vegetation/T2.md): the <c>--terminals</c> section of the drainage bake report. Read-only: it measures how stems end
/// (terminal kinds, the water the fine terrain shows in the lake a stem ends in, dry pans, lakes without an outlet river, heads and the D6 minimum) and how lakes and
/// rivers meet barrier cells (emitter sites, Wall Engine forecourts, the Weather Lung) and the rim rows. Nothing in the bake, the operator or the graph changes.
/// <c>--near=&lt;around km&gt;,&lt;across km&gt;,&lt;radius km&gt;</c> adds lists restricted to a disc (picture candidates).
/// </summary>
public partial class DrainageBakeReportTool
{
    /// <summary>Every lake stem is measured up to this count (the whole ring has about 1,500 and takes about 5 s); above it a hashed subset of this size.</summary>
    private const int TerminalHashedStems = 6000;
    private const long TerminalBatchPoints = 6_000_000;

    private sealed class LakeAreaJob
    {
        public int Lake, N, FloodedCells;
        public int[] Cols = Array.Empty<int>(), Rows = Array.Empty<int>();
        public bool[] Inside = Array.Empty<bool>();
        public long Offset;
        public long LakePoints, ChannelPoints;
        public double MaxDepth, PointArea;
        public long Points => (long)Cols.Length * N * N;
        public double LakeArea => LakePoints * PointArea;
        public double Area => (LakePoints + ChannelPoints) * PointArea;
    }

    private sealed class LakeStemRow
    {
        public int Stem, Lake;
        public bool InLake, Hashed, Near;
        public double AroundKm, AcrossKm, Width, Depth, Q, LengthKm;
        public LakeAreaJob Sample = null!;
        public int WetCells;
        public bool Closed;
        /// <summary>Discharge of every stem that ends in the lake, what the lake's balance holds (its recorded inflow), and the water area that evaporates the rivers' discharge at the pit cell's dryness.</summary>
        public double RiversQ, HoldsQ, NeededArea;
        /// <summary>A closed lake whose rivers bring more than its balance holds: a full pool that passes its surplus over a saddle to a neighbouring pool, with no channel.</summary>
        public bool Overflow => Closed && RiversQ > 1.25 * HoldsQ && RiversQ - HoldsQ > 1d;
    }

    private static ulong TerminalMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static double Quantile(List<double> sorted, double q) => sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * (sorted.Count - 1) + 0.5))];

    private static string Three(IEnumerable<double> values, string format = "G4")
    {
        List<double> v = values.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (v.Count == 0) return "-";
        return string.Join("/", new[] { 0.1, 0.5, 0.9 }.Select(q => Quantile(v, q).ToString(format, CultureInfo.InvariantCulture)));
    }

    /// <summary>p10/p50/p90 of the values, each weighted by its length.</summary>
    private static string ThreeWeighted(List<(double V, double W)> values, string format = "G4")
    {
        if (values.Count == 0) return "-";
        values.Sort((a, b) => a.V.CompareTo(b.V));
        double total = values.Sum(x => x.W), run = 0;
        var result = new double[3];
        double[] q = { 0.1, 0.5, 0.9 };
        int k = 0;
        foreach ((double v, double w) in values) { run += w; while (k < 3 && run >= q[k] * total) result[k++] = v; }
        while (k < 3) result[k++] = values[^1].V;
        return string.Join("/", result.Select(x => x.ToString(format, CultureInfo.InvariantCulture)));
    }

    private static string TerminalsReport(RiverNetwork net, RiverGraph graph, DrainageField field, DrainageGrid grid, IDrainageHeightSource source,
        TerrainGenerationParameters recipe, int workers, string nearText, string dir)
    {
        var sb = new StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CultureInfo ci = CultureInfo.InvariantCulture;
        DrainageGridSpec spec = grid.Spec;
        RiverSpanTable table = net.SpanTable ?? throw new InvalidOperationException("--terminals needs the spans (a network built with levels)");
        var op = new RiverCarveOperator(graph);
        int ringColumns = spec.RingColumns, rows = spec.Rows, width = field.Width;
        double aroundStep = spec.AroundStepMeters, acrossStep = spec.AcrossStepMeters, cellArea = field.CellAreaKm2, cellAreaM2 = aroundStep * acrossStep;
        long circumference = spec.CircumferenceMeters;
        int acrossMax = (int)Math.Floor(net.WidthM) - 1;

        int[] down = field.Down.ToArray(), areaCells = field.AreaCells.ToArray(), lakeId = field.LakeId.ToArray(), basinId = field.BasinId.ToArray();
        float[] lowest = field.Lowest.ToArray(), moisture = field.Moisture.ToArray();
        double[] runoffAccumulation = field.RunoffAccumulation.ToArray();
        DrainageCellKind[] kind = field.Kind.ToArray();
        DrainageLake[] bakeLakes = field.Lakes.ToArray();
        DrainageBasinInfo[] basins = field.Basins.ToArray();
        double threshold = 100d / (recipe.HasRivers ? recipe.GetExtensionField("river_density") : 1d);

        int FieldCell(int ringCol, int row)
        {
            int col = (ringCol - spec.ColumnStart + ringColumns) % ringColumns;
            return col < spec.Columns && (uint)row < (uint)rows ? row * width + col : -1;
        }
        (double AroundKm, double AcrossKm) CellKm(int cell) => ((spec.RingColumn(cell % width) + 0.5) * aroundStep / 1000d, (cell / width + 0.5) * acrossStep / 1000d);

        // --near
        bool hasNear = false;
        double nearAround = 0, nearAcross = 0, nearRadius = 0;
        if (nearText.Length > 0)
        {
            string[] parts = nearText.Split(',');
            nearAround = double.Parse(parts[0], ci) * 1000d; nearAcross = double.Parse(parts[1], ci) * 1000d; nearRadius = double.Parse(parts[2], ci) * 1000d;
            hasNear = true;
        }
        bool IsNear(double aroundKm, double acrossKm)
        {
            if (!hasNear) return false;
            double da = net.WrapDelta(aroundKm * 1000d - nearAround), dc = acrossKm * 1000d - nearAcross;
            return da * da + dc * dc <= nearRadius * nearRadius;
        }

        // Runs by lake.
        int lakeCount = graph.Lakes.Length;
        var runOffset = new int[lakeCount + 1];
        foreach (LakeRun run in graph.Runs) runOffset[run.LakeId + 1]++;
        for (int i = 0; i < lakeCount; i++) runOffset[i + 1] += runOffset[i];
        var runOfLake = new int[graph.Runs.Length];
        {
            int[] cursor = (int[])runOffset.Clone();
            for (int i = 0; i < graph.Runs.Length; i++) runOfLake[cursor[graph.Runs[i].LakeId]++] = i;
        }
        IEnumerable<(int Col, int Row)> LakeCells(int lake)
        {
            for (int i = runOffset[lake]; i < runOffset[lake + 1]; i++)
            {
                LakeRun run = graph.Runs[runOfLake[i]];
                for (int c = 0; c < run.Length; c++) yield return ((run.Col + c) % ringColumns, run.Row);
            }
        }
        var wetCells = new int[lakeCount];
        for (int lake = 0; lake < lakeCount; lake++)
            foreach ((int col, int row) in LakeCells(lake))
            {
                int cell = FieldCell(col, row);
                if (cell >= 0 && lowest[cell] < graph.Lakes[lake].Level) wetCells[lake]++;
            }

        // ------------------------------------------------------------------ sampled water area
        LakeAreaJob NewJob(int lake, int forceN)
        {
            var cols = new List<int>(); var rws = new List<int>(); var inside = new List<bool>();
            var seen = new HashSet<long>();
            foreach ((int col, int row) in LakeCells(lake)) { cols.Add(col); rws.Add(row); inside.Add(true); seen.Add((long)row * ringColumns + col); }
            int flooded = cols.Count;
            for (int i = 0; i < flooded; i++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int row = rws[i] + dy;
                    if ((uint)row >= (uint)rows) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int col = (cols[i] + dx + ringColumns) % ringColumns;
                        if (graph.Index.IsFlooded(col, row) || !seen.Add((long)row * ringColumns + col)) continue;
                        cols.Add(col); rws.Add(row); inside.Add(false);
                    }
                }
            int n = forceN > 0 ? forceN : cols.Count <= 200 ? 20 : cols.Count <= 2000 ? 10 : 5;
            return new LakeAreaJob { Lake = lake, N = n, FloodedCells = flooded, Cols = cols.ToArray(), Rows = rws.ToArray(), Inside = inside.ToArray(), PointArea = cellAreaM2 / (n * n) };
        }
        long sampledPoints = 0;
        void RunJobs(List<LakeAreaJob> jobs)
        {
            int i = 0;
            while (i < jobs.Count)
            {
                long points = 0;
                int j = i;
                while (j < jobs.Count && (j == i || points + jobs[j].Points <= TerminalBatchPoints)) { jobs[j].Offset = points; points += jobs[j].Points; j++; }
                var positions = new DrainagePoint[points];
                int first = i, end = j;
                Parallel.For(first, end, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
                {
                    LakeAreaJob job = jobs[k];
                    long at = job.Offset;
                    int n = job.N;
                    for (int c = 0; c < job.Cols.Length; c++)
                        for (int y = 0; y < n; y++)
                        {
                            int across = Math.Clamp((int)Math.Floor((job.Rows[c] + (y + 0.5) / n) * acrossStep + 0.5), 0, acrossMax);
                            for (int x = 0; x < n; x++)
                            {
                                long around = (long)Math.Floor((job.Cols[c] + (x + 0.5) / n) * aroundStep + 0.5);
                                if (around >= circumference) around -= circumference;
                                positions[at++] = new DrainagePoint(around, across);
                            }
                        }
                });
                var heights = new float[points];
                source.SamplePoints(positions, heights);
                Parallel.For(first, end, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
                {
                    LakeAreaJob job = jobs[k];
                    double level = graph.Lakes[job.Lake].Level;
                    int perCell = job.N * job.N;
                    long lakePoints = 0, channelPoints = 0;
                    double maxDepth = 0;
                    for (int c = 0; c < job.Cols.Length; c++)
                    {
                        bool inside = job.Inside[c];
                        long at = job.Offset + (long)c * perCell;
                        for (int p = 0; p < perCell; p++)
                        {
                            DrainagePoint pt = positions[at + p];
                            RiverOperatorResult r = op.Evaluate(pt.Around, pt.Across, heights[at + p], 0d);
                            if (r.Kind == InlandWaterKind.None || !double.IsFinite(r.WaterLevel) || !(r.Height < r.WaterLevel)) continue;
                            if (r.Kind == InlandWaterKind.Lake)
                            {
                                if (Math.Abs(r.WaterLevel - level) > 0.01) continue; // another lake's water on a shared ring cell
                                lakePoints++;
                                if (r.WaterLevel - r.Height > maxDepth) maxDepth = r.WaterLevel - r.Height;
                            }
                            else if (inside) channelPoints++; // the drowned channel through the flooded cells; the approach channel on the ring is the river, not the lake
                        }
                    }
                    job.LakePoints = lakePoints; job.ChannelPoints = channelPoints; job.MaxDepth = maxDepth;
                });
                sampledPoints += points;
                i = j;
            }
        }

        // ------------------------------------------------------------------ stems
        int stemCount = net.Stems.Length;
        var terminalCount = new int[7];
        var lakeStems = new List<int>();
        var deadStems = new List<int>();
        int inLakeLakeStems = 0;
        for (int i = 0; i < stemCount; i++)
        {
            RiverStem st = net.Stems[i];
            terminalCount[(int)st.Terminal]++;
            if (st.Terminal == RiverTerminal.Lake) { lakeStems.Add(i); if (net.Reaches[st.ReachStart].UpstreamLake == st.TerminalId) inLakeLakeStems++; }
            else if (st.Terminal == RiverTerminal.Dead) deadStems.Add(i);
        }
        int LastSpan(int stem) { int r = net.Stems[stem].ReachStart + net.Stems[stem].ReachCount - 1; return table.ReachFirst[r] + table.ReachCount[r] - 1; }
        (double AroundKm, double AcrossKm) StemEnd(int stem)
        {
            net.Centre(stem, net.Curves[stem].Length, out double a, out double c, out _, out _);
            return (net.WrapAround(a) / 1000d, c / 1000d);
        }
        // Discharge the stems bring to each lake, against what the lake's balance holds (S8b: a full pool inside a closed basin keeps its capacity and passes the surplus to a neighbouring pool).
        DrainageBalanceParameters balanceParameters = DrainageBalanceParameters.From(field.Report.LakeAbundance);
        var riversQ = new double[lakeCount];
        var stemsOfLake = new int[lakeCount];
        foreach (int stem in lakeStems) { riversQ[net.Stems[stem].TerminalId] += table.Spans[LastSpan(stem)].Q; stemsOfLake[net.Stems[stem].TerminalId]++; }
        double NeededArea(int lake, double q)
        {
            int pit = bakeLakes[lake].PitCell;
            double perKm2 = balanceParameters.E0 / balanceParameters.Lambda * Math.Clamp(1d - (pit >= 0 && pit < moisture.Length ? moisture[pit] : 0.5f), 0.05, 1d);
            return q / RiverHydraulics.DischargePerKm2 / perKm2 * 1e6;
        }
        bool allStems = lakeStems.Count <= TerminalHashedStems;
        var hashedSet = new HashSet<int>(allStems ? lakeStems : lakeStems.OrderBy(s => TerminalMix((ulong)s)).Take(TerminalHashedStems));
        var rowsList = new List<LakeStemRow>();
        foreach (int stem in lakeStems)
        {
            (double aKm, double cKm) = StemEnd(stem);
            bool hashed = hashedSet.Contains(stem), near = IsNear(aKm, cKm);
            if (!hashed && !near) continue;
            RiverStem st = net.Stems[stem];
            int span = LastSpan(stem);
            rowsList.Add(new LakeStemRow
            {
                Stem = stem, Lake = st.TerminalId, InLake = net.Reaches[st.ReachStart].UpstreamLake == st.TerminalId, Hashed = hashed, Near = near, AroundKm = aKm, AcrossKm = cKm,
                Width = 2d * table.HalfWidth[span], Depth = table.Depth[span], Q = table.Spans[span].Q, LengthKm = net.Curves[stem].Length / 1000d,
                WetCells = wetCells[st.TerminalId], Closed = bakeLakes[st.TerminalId].Closed,
                RiversQ = riversQ[st.TerminalId], HoldsQ = bakeLakes[st.TerminalId].Inflow * RiverHydraulics.DischargePerKm2, NeededArea = NeededArea(st.TerminalId, riversQ[st.TerminalId]),
            });
        }
        var jobOfLake = new Dictionary<int, LakeAreaJob>();
        foreach (LakeStemRow row in rowsList) if (!jobOfLake.ContainsKey(row.Lake)) jobOfLake[row.Lake] = NewJob(row.Lake, 0);
        var areaClock = System.Diagnostics.Stopwatch.StartNew();
        RunJobs(jobOfLake.Values.OrderBy(j => j.Lake).ToList());
        // Refine small results on a 10 m lattice: 2,500 m^2 per point cannot tell a puddle from nothing.
        var refine = new List<LakeAreaJob>();
        foreach (LakeAreaJob job in jobOfLake.Values.OrderBy(j => j.Lake))
            if (job.N == 20 && job.LakePoints + job.ChannelPoints < 16 && job.Cols.Length <= 60) refine.Add(NewJob(job.Lake, 100));
        RunJobs(refine);
        foreach (LakeAreaJob job in refine) jobOfLake[job.Lake] = job;
        double areaSeconds = areaClock.Elapsed.TotalSeconds;
        foreach (LakeStemRow row in rowsList) row.Sample = jobOfLake[row.Lake];

        sb.AppendLine(FormattableString.Invariant($"terminals (T2R-6c): stems {stemCount}; sea {terminalCount[(int)RiverTerminal.Sea]}, lake {terminalCount[(int)RiverTerminal.Lake]} (of them {inLakeLakeStems} are drowned channels that start inside the lake they end in, {lakeStems.Count - inLakeLakeStems} are rivers that end on a lake cell), confluence {terminalCount[(int)RiverTerminal.Confluence]}, lung {terminalCount[(int)RiverTerminal.Lung]}, strip end {terminalCount[(int)RiverTerminal.StripEnd]}, dead {terminalCount[(int)RiverTerminal.Dead]}"));
        int ends = terminalCount[(int)RiverTerminal.Sea] + terminalCount[(int)RiverTerminal.Lake] + terminalCount[(int)RiverTerminal.Lung] + terminalCount[(int)RiverTerminal.Dead];
        sb.AppendLine(FormattableString.Invariant($"river ends (stems that do not join another river): {ends}; sea {(double)terminalCount[(int)RiverTerminal.Sea] / Math.Max(1, ends):P1}, lake {(double)terminalCount[(int)RiverTerminal.Lake] / Math.Max(1, ends):P1}, lung {(double)terminalCount[(int)RiverTerminal.Lung] / Math.Max(1, ends):P1}, dead {(double)terminalCount[(int)RiverTerminal.Dead] / Math.Max(1, ends):P2}"));
        List<LakeStemRow> sample = rowsList.Where(r => r.Hashed).ToList();
        sb.AppendLine(FormattableString.Invariant($"sampled water area: {sample.Count} lake stems ({(allStems ? "all" : "hashed")}) plus {rowsList.Count - sample.Count} more inside the --near disc, {jobOfLake.Count} lakes, {sampledPoints} points, {areaSeconds:F1} s. Lattice over the flooded cells and their ring: 50 m (up to 200 cells), 100 m (up to 2,000), 200 m (larger); a lake with fewer than 16 wet points and at most 60 cells is measured again at 10 m ({refine.Count} lakes). A point is water when the production operator at footprint 0 reports a lake (at this lake's level) or, inside the flooded cells, a river, with the carved height below the level."));

        void StemStats(string label, List<LakeStemRow> list)
        {
            if (list.Count == 0) { sb.AppendLine($"  {label}: none"); return; }
            int zero = list.Count(r => r.Sample.Area <= 0), under = list.Count(r => r.Sample.Area < 4d * r.Width * r.Width), noLake = list.Count(r => r.Sample.LakeArea <= 0);
            List<LakeStemRow> withWet = list.Where(r => r.WetCells > 0).ToList();
            sb.AppendLine(FormattableString.Invariant($"  {label}: {list.Count} stems; area / area that evaporates the rivers' discharge p10/p50/p90 {Three(list.Select(r => r.Sample.Area / Math.Max(1d, r.NeededArea)), "F3")}, under 0.1: {list.Count(r => r.Sample.Area < 0.1 * r.NeededArea)}, under 0.5: {list.Count(r => r.Sample.Area < 0.5 * r.NeededArea)}; overflowing {list.Count(r => r.Overflow)}; area / (wet cells x cell area) p10/p50/p90 {Three(withWet.Select(r => r.Sample.Area / (r.WetCells * cellAreaM2)), "F3")} ({withWet.Count} with a wet cell); area / w^2 p10/p50/p90 {Three(list.Select(r => r.Sample.Area / (r.Width * r.Width)), "F1")}; area p10/p50/p90 {Three(list.Select(r => r.Sample.Area), "F0")} m^2; under 4 w^2: {under} ({(double)under / list.Count:P1}); no water at all: {zero} ({(double)zero / list.Count:P1}); no lake water outside the channel: {noLake} ({(double)noLake / list.Count:P1}); w p10/p50/p90 {Three(list.Select(r => r.Width), "F1")} m"));
        }
        sb.AppendLine("lake stems, sampled water area of the lake each ends in:");
        StemStats("all", sample);
        StemStats("closed (balance) lakes", sample.Where(r => r.Closed).ToList());
        StemStats("spilling lakes", sample.Where(r => !r.Closed).ToList());
        StemStats("drowned channels (start inside the lake)", sample.Where(r => r.InLake).ToList());
        StemStats("rivers ending on a lake cell", sample.Where(r => !r.InLake).ToList());
        StemStats("lakes of 1 flooded cell", sample.Where(r => r.Sample.FloodedCells == 1).ToList());
        StemStats("lakes of 2-4 flooded cells", sample.Where(r => r.Sample.FloodedCells is >= 2 and <= 4).ToList());
        StemStats("lakes of 5-20 flooded cells", sample.Where(r => r.Sample.FloodedCells is >= 5 and <= 20).ToList());
        StemStats("lakes of 21 or more flooded cells", sample.Where(r => r.Sample.FloodedCells > 20).ToList());
        StemStats("closed lakes that overflow (rivers bring over 1.25 times what the balance holds)", sample.Where(r => r.Overflow).ToList());
        StemStats("closed lakes that do not overflow", sample.Where(r => r.Closed && !r.Overflow).ToList());
        {
            // Every lake stem, not only the sample: the overflow that has no channel.
            int overflowLakes = 0, overflowStems = 0, closedLakes = 0, closedStems = 0;
            double lost = 0, brought = 0;
            var lostShare = new List<double>(); var lostQ = new List<double>();
            for (int lake = 0; lake < lakeCount; lake++)
            {
                if (stemsOfLake[lake] == 0 || !bakeLakes[lake].Closed) continue;
                closedLakes++; closedStems += stemsOfLake[lake];
                double holds = bakeLakes[lake].Inflow * RiverHydraulics.DischargePerKm2;
                brought += riversQ[lake];
                if (!(riversQ[lake] > 1.25 * holds && riversQ[lake] - holds > 1d)) continue;
                overflowLakes++; overflowStems += stemsOfLake[lake];
                lost += riversQ[lake] - holds;
                lostShare.Add((riversQ[lake] - holds) / riversQ[lake]); lostQ.Add(riversQ[lake] - holds);
            }
            sb.AppendLine(FormattableString.Invariant($"overflow without a channel (every lake stem): closed lakes that a stem ends in {closedLakes} ({closedStems} stems, {brought:F0} m3/s); of them {overflowLakes} lakes ({overflowStems} stems) take in over 1.25 times what their balance holds; discharge that leaves them with no channel {lost:F0} m3/s ({lost / Math.Max(1d, brought):P1} of what the stems bring to closed lakes), per lake p10/p50/p90 {Three(lostQ, "F1")} m3/s, share of the rivers' discharge p10/p50/p90 {Three(lostShare, "F2")}"));
            // Causes of the sampled lake stems, in this order: the lake is as large as its flow; overflow; a spilling lake whose water goes on; the balance holds the flow but the fine terrain shows less.
            int fine = 0, overflow = 0, spillOn = 0, spillLost = 0, small = 0;
            foreach (LakeStemRow r in sample)
            {
                if (!r.Closed) { if (graph.Lakes[r.Lake].OutletStem >= 0) spillOn++; else spillLost++; }
                else if (r.Sample.Area >= 0.5 * r.NeededArea) fine++;
                else if (r.Overflow) overflow++;
                else small++;
            }
            double n = Math.Max(1, sample.Count);
            sb.AppendLine(FormattableString.Invariant($"sampled lake stems by what the end shows: closed lake with at least half the area that evaporates its rivers {fine} ({fine / n:P1}); closed lake that overflows with no channel {overflow} ({overflow / n:P1}); closed lake that holds the flow in the balance but shows under half that area on the fine terrain {small} ({small / n:P1}); spilling lake with an outlet river {spillOn} ({spillOn / n:P1}); spilling lake without an outlet river {spillLost} ({spillLost / n:P1})"));
        }
        {
            List<int> lakes = sample.Select(r => r.Lake).Distinct().ToList();
            long flooded = lakes.Sum(l => (long)graph.Lakes[l].CellCount), wet = lakes.Sum(l => (long)wetCells[l]);
            int noWet = lakes.Count(l => wetCells[l] == 0), shallow1 = lakes.Count(l => graph.Lakes[l].Depth <= 1.0001f), shallow2 = lakes.Count(l => graph.Lakes[l].Depth <= 2.0001f);
            sb.AppendLine(FormattableString.Invariant($"  the {lakes.Count} lakes of these stems: flooded cells {flooded}, of them with the lowest sample at or above the level {flooded - wet} ({(double)(flooded - wet) / Math.Max(1, flooded):P1}); lakes with no cell under the level {noWet}; level at most 1 m above the lowest sample {shallow1} ({(double)shallow1 / Math.Max(1, lakes.Count):P1}), at most 2 m {shallow2} ({(double)shallow2 / Math.Max(1, lakes.Count):P1}); closed {lakes.Count(l => bakeLakes[l].Closed)}, spilling {lakes.Count(l => !bakeLakes[l].Closed)}; without an outlet river {lakes.Count(l => graph.Lakes[l].OutletStem < 0)}"));
        }
        string StemLine(LakeStemRow r)
        {
            RiverLake lake = graph.Lakes[r.Lake];
            return FormattableString.Invariant($"stem {r.Stem} end (around {r.AroundKm:F3} km, across {r.AcrossKm:F3} km) w {r.Width:F1} m d {r.Depth:F1} m Q {r.Q:F1} m3/s length {r.LengthKm:F1} km {(r.InLake ? "drowned channel" : "river")}; lake {r.Lake} {(r.Closed ? "closed" : "spilling")} level {lake.Level:F0} m, depth to lowest sample {lake.Depth:F1} m, flooded {lake.CellCount} cells, wet {r.WetCells}, holds {r.HoldsQ:F1} m3/s, its rivers bring {r.RiversQ:F1} m3/s{(r.Overflow ? " (overflows)" : "")}, outlet stem {lake.OutletStem}; area that evaporates the rivers {r.NeededArea:F0} m^2; water area {r.Sample.Area:F0} m^2 (lake {r.Sample.LakeArea:F0}, channel {r.Sample.Area - r.Sample.LakeArea:F0}; lattice {1000d / r.Sample.N:F0} m), deepest {r.Sample.MaxDepth:F1} m, area / w^2 {r.Sample.Area / (r.Width * r.Width):F1}");
        }
        sb.AppendLine("the 20 smallest by water area / w^2:");
        foreach (LakeStemRow r in sample.OrderBy(r => r.Sample.Area / (r.Width * r.Width)).ThenBy(r => r.Stem).Take(20)) sb.AppendLine("  " + StemLine(r));
        sb.AppendLine("the 10 largest overflows (discharge that leaves the lake with no channel):");
        foreach (LakeStemRow r in sample.Where(r => r.Overflow).OrderByDescending(r => r.RiversQ - r.HoldsQ).ThenBy(r => r.Stem).Take(10)) sb.AppendLine("  " + StemLine(r));
        if (hasNear)
        {
            List<LakeStemRow> near = rowsList.Where(r => r.Near).ToList();
            sb.AppendLine(FormattableString.Invariant($"lake stems within {nearRadius / 1000d:F0} km of (around {nearAround / 1000d:F1} km, across {nearAcross / 1000d:F1} km): {near.Count}"));
            StemStats("near disc", near);
            sb.AppendLine("  the 12 smallest by water area / w^2:");
            foreach (LakeStemRow r in near.OrderBy(r => r.Sample.Area / (r.Width * r.Width)).ThenBy(r => r.Stem).Take(12)) sb.AppendLine("  " + StemLine(r));
            sb.AppendLine("  the 8 largest overflows:");
            foreach (LakeStemRow r in near.Where(r => r.Overflow).OrderByDescending(r => r.RiversQ - r.HoldsQ).ThenBy(r => r.Stem).Take(8)) sb.AppendLine("  " + StemLine(r));
            sb.AppendLine("  the 8 widest rivers that end in a lake under a tenth of the area that evaporates them:");
            foreach (LakeStemRow r in near.Where(r => r.Closed && r.Sample.Area < 0.1 * r.NeededArea).OrderByDescending(r => r.Width).ThenBy(r => r.Stem).Take(8)) sb.AppendLine("  " + StemLine(r));
        }
        {
            var csv = new StringBuilder("stem,around_km,across_km,in_lake,hashed,near,w_m,d_m,q_m3s,length_km,lake,closed,level_m,depth_m,flooded_cells,wet_cells,holds_m3s,rivers_m3s,needed_area_m2,outlet_stem,lattice_m,water_area_m2,lake_area_m2,deepest_m\n");
            foreach (LakeStemRow r in rowsList)
            {
                RiverLake lake = graph.Lakes[r.Lake];
                csv.AppendLine(FormattableString.Invariant($"{r.Stem},{r.AroundKm:F3},{r.AcrossKm:F3},{(r.InLake ? 1 : 0)},{(r.Hashed ? 1 : 0)},{(r.Near ? 1 : 0)},{r.Width:F2},{r.Depth:F2},{r.Q:F2},{r.LengthKm:F2},{r.Lake},{(r.Closed ? 1 : 0)},{lake.Level:F0},{lake.Depth:F2},{lake.CellCount},{r.WetCells},{r.HoldsQ:F2},{r.RiversQ:F2},{r.NeededArea:F0},{lake.OutletStem},{1000d / r.Sample.N:F0},{r.Sample.Area:F0},{r.Sample.LakeArea:F0},{r.Sample.MaxDepth:F2}"));
            }
            File.WriteAllText(Path.Combine(dir, "terminals_lake_stems.csv"), csv.ToString());
        }

        // ------------------------------------------------------------------ drowned channels over dry ground
        {
            var points = new List<DrainagePoint>();
            var levels = new List<float>();
            int stems = 0;
            double km = 0;
            for (int i = 0; i < stemCount; i++)
            {
                double end = i < net.InLakeEnd.Length ? net.InLakeEnd[i] : 0d;
                if (end <= 0d) continue;
                int lake = net.Reaches[net.Stems[i].ReachStart].UpstreamLake;
                if (lake < 0) continue;
                stems++; km += end / 1000d;
                for (double s = 25d; s < end; s += 50d)
                {
                    net.Centre(i, s, out double a, out double c, out _, out _);
                    points.Add(new DrainagePoint((long)Math.Floor(net.WrapAround(a) + 0.5) % circumference, Math.Clamp((int)Math.Floor(c + 0.5), 0, acrossMax)));
                    levels.Add(graph.Lakes[lake].Level);
                }
            }
            var ground = new float[points.Count];
            if (points.Count > 0) source.SamplePoints(points.ToArray(), ground);
            int above = 0, above2 = 0, above10 = 0;
            for (int i = 0; i < ground.Length; i++) { if (ground[i] >= levels[i]) above++; if (ground[i] >= levels[i] + 2f) above2++; if (ground[i] >= levels[i] + 10f) above10++; }
            sb.AppendLine(FormattableString.Invariant($"drowned channels (T2R-8b): {stems} stems start inside a lake, {km:F0} km in the lake; natural ground on the centreline every 50 m ({ground.Length} stations): at or above the lake level on {(double)above / Math.Max(1, ground.Length):P1}, 2 m or more above on {(double)above2 / Math.Max(1, ground.Length):P1}, 10 m or more above on {(double)above10 / Math.Max(1, ground.Length):P1} (there the channel is a cut through ground the lake does not cover)"));
        }

        // ------------------------------------------------------------------ dead stems
        {
            DrainageBalanceParameters balance = balanceParameters;
            sb.AppendLine($"dead stems: {deadStems.Count}");
            int pans = 0, clampEmpty = 0, other = 0;
            var lines = new List<string>();
            foreach (int stem in deadStems)
            {
                RiverStem st = net.Stems[stem];
                (double aKm, double cKm) = StemEnd(stem);
                int span = LastSpan(stem), last = st.LastCell, next = last >= 0 ? down[last] : -1;
                string cause;
                if (next < 0) { cause = "the last cell has no downstream cell"; other++; }
                else
                {
                    int basin = basinId[next];
                    double evaporation = cellArea * balance.E0 / balance.Lambda * Math.Clamp(1d - moisture[next], 0.05, 1d), inflow = runoffAccumulation[next];
                    if (basin >= 0 && basins[basin].Closed && down[next] < 0)
                    {
                        if (inflow < 0.5 * evaporation) { cause = FormattableString.Invariant($"dry pan: inflow {inflow * RiverHydraulics.DischargePerKm2:F1} m3/s ({inflow:F1} units) is under half a cell's evaporation ({0.5 * evaporation:F1} units, moisture {moisture[next]:F2})"); pans++; }
                        else { cause = FormattableString.Invariant($"dry pan: inflow {inflow:F1} units against {evaporation:F1} per cell, but the whole-metre level has no room (lowest sample {lowest[next]:F1} m, basin spill {basins[basin].SpillLevel:F0} m)"); clampEmpty++; }
                    }
                    else { cause = FormattableString.Invariant($"next cell kind {kind[next]}, area {areaCells[next] * cellArea:F0} km^2, basin {basin}{(basin >= 0 ? (basins[basin].Closed ? " (closed)" : " (spilling)") : "")}, downstream {down[next]}, lake {lakeId[next]}"); other++; }
                }
                bool near = IsNear(aKm, cKm);
                lines.Add(FormattableString.Invariant($"  stem {stem} end (around {aKm:F3} km, across {cKm:F3} km){(near ? " [near]" : "")} w {2d * table.HalfWidth[span]:F1} m d {table.Depth[span]:F1} m Q {table.Spans[span].Q:F1} m3/s length {net.Curves[stem].Length / 1000d:F1} km: {cause}"));
            }
            sb.AppendLine($"  causes: dry pan under half a cell's evaporation {pans}, dry pan with an empty level clamp {clampEmpty}, other {other}");
            foreach (string line in lines.Take(60)) sb.AppendLine(line);
        }

        // ------------------------------------------------------------------ spilling lakes without an outlet river
        {
            int spilling = 0, noOutlet = 0, noOutletInlets = 0;
            var exits = new Dictionary<string, int>();
            var lines = new List<(double Inflow, string Text)>();
            for (int lake = 0; lake < lakeCount; lake++)
            {
                if (bakeLakes[lake].Closed) continue;
                spilling++;
                if (graph.Lakes[lake].OutletStem >= 0) continue;
                noOutlet++;
                if (graph.Lakes[lake].InletCount == 0) continue;
                noOutletInlets++;
                int exitFrom = -1;
                foreach ((int col, int row) in LakeCells(lake))
                {
                    int cell = FieldCell(col, row);
                    if (cell < 0 || down[cell] < 0 || lakeId[down[cell]] == lake) continue;
                    if (exitFrom < 0 || areaCells[cell] > areaCells[exitFrom]) exitFrom = cell;
                }
                string exit;
                int target = exitFrom >= 0 ? down[exitFrom] : -1;
                if (target < 0) exit = "no exit cell";
                else if (kind[target] == DrainageCellKind.Ocean) exit = "the sea";
                else if (kind[target] == DrainageCellKind.Lung) exit = "the Weather Lung";
                else if (kind[target] == DrainageCellKind.StripOutlet) exit = "the strip end";
                else if (lakeId[target] >= 0) exit = "another lake";
                else if (areaCells[target] * cellArea >= threshold - 1e-9) exit = "a cell that carries a river";
                else exit = "a land cell under the channel threshold";
                exits[exit] = exits.GetValueOrDefault(exit) + 1;
                (double aKm, double cKm) = exitFrom >= 0 ? CellKm(exitFrom) : (0d, 0d);
                double outflow = bakeLakes[lake].Basin >= 0 && bakeLakes[lake].Basin < basins.Length ? basins[bakeLakes[lake].Basin].Outflow : double.NaN;
                lines.Add((graph.Lakes[lake].Inflow, FormattableString.Invariant($"  lake {lake} exit cell (around {aKm:F1} km, across {cKm:F1} km){(IsNear(aKm, cKm) ? " [near]" : "")} level {graph.Lakes[lake].Level:F0} m, {graph.Lakes[lake].CellCount} cells, inlets {graph.Lakes[lake].InletCount}, inflow {graph.Lakes[lake].Inflow * RiverHydraulics.DischargePerKm2:F1} m3/s, outflow {outflow * RiverHydraulics.DischargePerKm2:F1} m3/s, spills into {exit}{(target >= 0 ? FormattableString.Invariant($" (area {areaCells[target] * cellArea:F0} km^2)") : "")}")));
            }
            sb.AppendLine(FormattableString.Invariant($"spilling lakes: {spilling}; without an outlet river {noOutlet}; of those with at least one inlet river {noOutletInlets}; they spill into: {string.Join(", ", exits.OrderByDescending(e => e.Value).Select(e => $"{e.Key} {e.Value}"))}"));
            foreach ((double _, string text) in lines.OrderByDescending(l => l.Inflow).Take(10)) sb.AppendLine(text);
        }

        // ------------------------------------------------------------------ heads and the D6 minimum
        {
            RiverDangerConstants constants = RiverDangerConstants.Default;
            RiverDangerConstants free = constants with { MinDepth = 0.001, MinWidth = 0.001 };
            double minWidth = constants.MinWidth + 1e-3, minDepth = constants.MinDepth + 1e-3;
            int sources = 0, outlets = 0, inLake = 0, sourcesAtWidth = 0, sourcesAtDepth = 0;
            var headQ = new List<double>(); var headFreeWidth = new List<double>(); var headFreeDepth = new List<double>();
            for (int i = 0; i < stemCount; i++)
            {
                RiverStem st = net.Stems[i];
                if (i < net.InLakeEnd.Length && net.InLakeEnd[i] > 0d) { inLake++; continue; }
                if (net.Reaches[st.ReachStart].UpstreamLake >= 0) { outlets++; continue; }
                sources++;
                int span = table.ReachFirst[st.ReachStart];
                if (2d * table.HalfWidth[span] <= minWidth) sourcesAtWidth++;
                if (table.Depth[span] <= minDepth) sourcesAtDepth++;
                RiverSpan sp = table.Spans[span];
                headQ.Add(sp.Q);
                if (sp.Q > 1e-6)
                {
                    RiverDangerChannel ch = RiverDanger.Channel(free, sp.Q, Math.Max(sp.Sh, 1e-4f), net.ChannelDepth, net.Ferocity, sp.Landing);
                    headFreeWidth.Add(ch.Width); headFreeDepth.Add(ch.Depth);
                }
            }
            if (hasNear)
            {
                var nearHeads = new List<(int Stem, double AroundKm, double AcrossKm, double Width, double Depth, double Q)>();
                for (int i = 0; i < stemCount; i++)
                {
                    RiverStem st = net.Stems[i];
                    if ((i < net.InLakeEnd.Length && net.InLakeEnd[i] > 0d) || net.Reaches[st.ReachStart].UpstreamLake >= 0) continue;
                    net.Centre(i, 0d, out double a, out double c, out _, out _);
                    double aKm = net.WrapAround(a) / 1000d, cKm = c / 1000d;
                    if (!IsNear(aKm, cKm)) continue;
                    int span = table.ReachFirst[st.ReachStart];
                    nearHeads.Add((i, aKm, cKm, 2d * table.HalfWidth[span], table.Depth[span], table.Spans[span].Q));
                }
                sb.AppendLine($"sources inside the --near disc: {nearHeads.Count}; the five widest and the five nearest the centre:");
                foreach (var h in nearHeads.OrderByDescending(h => h.Width).ThenBy(h => h.Stem).Take(5).Concat(nearHeads.OrderBy(h => { double da = net.WrapDelta(h.AroundKm * 1000d - nearAround), dc = h.AcrossKm * 1000d - nearAcross; return da * da + dc * dc; }).Take(5)))
                    sb.AppendLine(FormattableString.Invariant($"  stem {h.Stem} head (around {h.AroundKm:F3} km, across {h.AcrossKm:F3} km) w {h.Width:F1} m d {h.Depth:F1} m Q {h.Q:F1} m3/s, length {net.Curves[h.Stem].Length / 1000d:F1} km"));
            }
            sb.AppendLine(FormattableString.Invariant($"heads: {sources} stems start at the channel threshold (a source), {outlets} at a lake outlet, {inLake} inside a lake (drowned channels and outlet leads); sources at the minimum width {sourcesAtWidth} ({(double)sourcesAtWidth / Math.Max(1, sources):P1}), at the minimum depth {sourcesAtDepth} ({(double)sourcesAtDepth / Math.Max(1, sources):P1}); Q at the source p10/p50/p90 {Three(headQ, "F1")} m3/s; without the D6 floors the source would be {Three(headFreeWidth, "F1")} m wide and {Three(headFreeDepth, "F2")} m deep (p10/p50/p90)"));
            double total = 0, atWidth = 0, atDepth = 0, atBoth = 0, atEither = 0;
            var freeWidth = new List<(double, double)>(); var freeDepth = new List<(double, double)>(); var qAtWidth = new List<(double, double)>(); var qAtDepth = new List<(double, double)>();
            var freeDepthAtWidth = new List<(double, double)>();
            for (int i = 0; i < table.Count; i++)
            {
                RiverSpan sp = table.Spans[i];
                int stem = net.Reaches[sp.Reach].Stem;
                if (stem < net.InLakeEnd.Length && net.InLakeEnd[stem] > 0d && sp.SEnd <= net.InLakeEnd[stem] + 1e-3) continue;
                double len = sp.SEnd - sp.SStart;
                total += len;
                bool w = 2d * table.HalfWidth[i] <= minWidth, d = table.Depth[i] <= minDepth;
                if (w) atWidth += len;
                if (d) atDepth += len;
                if (w && d) atBoth += len;
                if (w || d) atEither += len;
                if (!(w || d) || sp.Q <= 1e-6) continue;
                RiverDangerChannel ch = RiverDanger.Channel(free, sp.Q, Math.Max(sp.Sh, 1e-4f), net.ChannelDepth, net.Ferocity, sp.Landing);
                if (w) { freeWidth.Add((ch.Width, len)); qAtWidth.Add((sp.Q, len)); freeDepthAtWidth.Add((ch.Depth, len)); }
                if (d) { freeDepth.Add((ch.Depth, len)); qAtDepth.Add((sp.Q, len)); }
            }
            sb.AppendLine(FormattableString.Invariant($"D6 minimum (width {constants.MinWidth:F0} m, depth {constants.MinDepth:F1} m), by span length without the in-lake parts: channel {total / 1000d:F0} km; at the minimum width {atWidth / 1000d:F0} km ({atWidth / Math.Max(1, total):P1}); at the minimum depth {atDepth / 1000d:F0} km ({atDepth / Math.Max(1, total):P1}); at both {atBoth / 1000d:F0} km ({atBoth / Math.Max(1, total):P1}); at either {atEither / 1000d:F0} km ({atEither / Math.Max(1, total):P1})"));
            sb.AppendLine(FormattableString.Invariant($"  where the width is at the minimum: Q p10/p50/p90 {ThreeWeighted(qAtWidth, "F1")} m3/s; with both floors lifted the same law gives width {ThreeWeighted(freeWidth, "F1")} m and depth {ThreeWeighted(freeDepthAtWidth, "F2")} m. Where the depth is at the minimum: Q {ThreeWeighted(qAtDepth, "F1")} m3/s; lifted depth {ThreeWeighted(freeDepth, "F2")} m"));
        }
        sb.AppendLine(FormattableString.Invariant($"terminals section {clock.Elapsed.TotalSeconds:F1} s"));

        // ================================================================== T2R-17: barriers and rim rows
        clock.Restart();
        EmitterSiteTerrainShaper? shaper = recipe.HasEmitterSites ? EmitterSiteTerrainShaper.GetOrCreate(recipe) : null;
        (int Site, double Distance, double Influence) NearestSite(double around, double across)
        {
            if (shaper == null || !shaper.Registry.TryGetNearestSite((long)Math.Floor(around), across, out int index, out double du, out double dw)) return (-1, double.NaN, double.NaN);
            return (index, Math.Sqrt(du * du + dw * dw), shaper.Registry[index].InfluenceRadius);
        }
        {
            // Lakes with a ring cell that is a barrier cell.
            var perLake = new List<(int Lake, double Max, int Cell, int Below, int Count)>();
            long floodedBarrier = 0;
            double minFloodClearance = double.PositiveInfinity; int minFloodLake = -1, minFloodSite = -1;
            var seen = new HashSet<int>();
            for (int lake = 0; lake < lakeCount; lake++)
            {
                double max = double.NegativeInfinity; int maxCell = -1, below = 0, count = 0;
                seen.Clear();
                foreach ((int col, int row) in LakeCells(lake))
                {
                    int self = FieldCell(col, row);
                    if (self >= 0 && kind[self] == DrainageCellKind.Barrier) floodedBarrier++;
                    if (shaper != null)
                    {
                        double ca = (col + 0.5) * aroundStep, cc = (row + 0.5) * acrossStep;
                        if (shaper.Registry.TryGetNearestSite((long)ca, cc, out int site, out double du, out double dw))
                        {
                            double ex = Math.Max(0d, Math.Abs(du) - 0.5 * aroundStep), ey = Math.Max(0d, Math.Abs(dw) - 0.5 * acrossStep);
                            double clearance = Math.Sqrt(ex * ex + ey * ey) - shaper.Registry[site].InfluenceRadius;
                            if (clearance < minFloodClearance) { minFloodClearance = clearance; minFloodLake = lake; minFloodSite = site; }
                        }
                    }
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int r = row + dy, c = (col + dx + ringColumns) % ringColumns;
                            if ((uint)r >= (uint)rows || graph.Index.IsFlooded(c, r)) continue;
                            int cell = FieldCell(c, r);
                            if (cell < 0 || kind[cell] != DrainageCellKind.Barrier || !seen.Add(cell)) continue;
                            count++;
                            double diff = graph.Lakes[lake].Level - lowest[cell];
                            if (diff > 0) below++;
                            if (diff > max) { max = diff; maxCell = cell; }
                        }
                }
                if (count > 0) perLake.Add((lake, max, maxCell, below, count));
            }
            List<double> diffs = perLake.Select(p => p.Max).OrderBy(x => x).ToList();
            sb.AppendLine(FormattableString.Invariant($"barriers (T2R-17): lakes with a ring cell that is a barrier cell {perLake.Count} of {lakeCount}; per lake the largest (level - lowest sample of that ring cell): p50 {Quantile(diffs, 0.5):F1} m, p90 {Quantile(diffs, 0.9):F1} m, max {Quantile(diffs, 1):F1} m; above 0 m (the lake stands over barrier ground) {perLake.Count(p => p.Max > 0)}, over 5 m {perLake.Count(p => p.Max > 5)}, over 25 m {perLake.Count(p => p.Max > 25)}; barrier ring cells {perLake.Sum(p => p.Count)}, of them below the lake level {perLake.Sum(p => p.Below)} (each is up to 1 km of bank along a cell line)"));
            sb.AppendLine(FormattableString.Invariant($"  flooded cells that are barrier cells: {floodedBarrier} (must be 0); smallest clearance between a flooded cell and an emitter site's influence disc: {minFloodClearance:F0} m (lake {minFloodLake}, site {minFloodSite})"));

            // The fine terrain on one barrier ring cell: the plug the operator builds and the water that stands in the cell.
            string Fine(int lake, int cell)
            {
                const int n = 20;
                var pts = new DrainagePoint[n * n];
                int ringCol = spec.RingColumn(cell % width), row = cell / width;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        long around = (long)Math.Floor((ringCol + (x + 0.5) / n) * aroundStep + 0.5);
                        if (around >= circumference) around -= circumference;
                        pts[y * n + x] = new DrainagePoint(around, Math.Clamp((int)Math.Floor((row + (y + 0.5) / n) * acrossStep + 0.5), 0, acrossMax));
                    }
                var h = new float[pts.Length];
                source.SamplePoints(pts, h);
                double maxRaise = 0, level = graph.Lakes[lake].Level, minGround = double.PositiveInfinity;
                int raised = 0, wet = 0, under = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    RiverOperatorResult r = op.Evaluate(pts[i].Around, pts[i].Across, h[i], 0d);
                    double raise = r.Height - h[i];
                    if (raise > maxRaise) maxRaise = raise;
                    if (raise > 1d) raised++;
                    if (h[i] < level) under++;
                    if (h[i] < minGround) minGround = h[i];
                    if (r.Kind != InlandWaterKind.None && double.IsFinite(r.WaterLevel) && r.Height < r.WaterLevel) wet++;
                }
                return FormattableString.Invariant($"on a 50 m lattice of that cell: natural ground under the level on {(double)under / pts.Length:P0} (lowest {minGround:F1} m), water on {(double)wet / pts.Length:P0}, ground raised by more than 1 m on {(double)raised / pts.Length:P0}, largest raise {maxRaise:F1} m");
            }
            string PlugLine((int Lake, double Max, int Cell, int Below, int Count) p)
            {
                (double aKm, double cKm) = CellKm(p.Cell);
                (int site, double distance, double influence) = NearestSite(aKm * 1000d, cKm * 1000d);
                RiverLake lake = graph.Lakes[p.Lake];
                string siteText = site >= 0 ? FormattableString.Invariant($"emitter site {site} at {distance:F0} m (influence radius {influence:F0} m{(shaper!.Registry[site].IsIsland ? ", island" : "")}{(shaper.Registry[site].IsMain ? ", main" : "")})") : "no emitter site";
                return FormattableString.Invariant($"  lake {p.Lake} {(bakeLakes[p.Lake].Closed ? "closed" : "spilling")} level {lake.Level:F0} m, {lake.CellCount} cells; barrier ring cell (around {aKm:F1} km, across {cKm:F1} km){(IsNear(aKm, cKm) ? " [near]" : "")} lowest sample {lowest[p.Cell]:F1} m, level above it {p.Max:F1} m; {p.Below} of {p.Count} barrier ring cells below the level; nearest {siteText}; {Fine(p.Lake, p.Cell)}");
            }
            sb.AppendLine("  the ten largest:");
            foreach (var p in perLake.OrderByDescending(p => p.Max).ThenBy(p => p.Lake).Take(10)) sb.AppendLine(PlugLine(p));
            List<(int Lake, double Max, int Cell, int Below, int Count)> positive = perLake.Where(p => p.Max > 0).OrderBy(p => p.Max).ToList();
            if (positive.Count > 0)
            {
                sb.AppendLine("  typical (the median of those above 0 m, and the two next to it):");
                for (int k = Math.Max(0, positive.Count / 2 - 1); k <= Math.Min(positive.Count - 1, positive.Count / 2 + 1); k++) sb.AppendLine(PlugLine(positive[k]));
                sb.AppendLine("  the five with the most barrier ring cells below the level (the longest straight banks):");
                foreach (var p in positive.OrderByDescending(p => p.Below).ThenBy(p => p.Lake).Take(5)) sb.AppendLine(PlugLine(p));
            }
            if (hasNear)
            {
                List<(int Lake, double Max, int Cell, int Below, int Count)> near = perLake.Where(p => { (double a, double c) = CellKm(p.Cell); return IsNear(a, c); }).OrderByDescending(p => p.Max).ToList();
                sb.AppendLine($"  inside the --near disc: {near.Count} lakes with a barrier ring cell, {near.Count(p => p.Max > 0)} above 0 m; the eight largest:");
                foreach (var p in near.Take(8)) sb.AppendLine(PlugLine(p));
            }
            var csv = new StringBuilder("lake,closed,level_m,cells,around_km,across_km,lowest_m,level_above_m,barrier_ring_cells,below_level,site,site_distance_m,site_influence_m\n");
            foreach (var p in perLake.OrderByDescending(p => p.Max))
            {
                (double aKm, double cKm) = CellKm(p.Cell);
                (int site, double distance, double influence) = NearestSite(aKm * 1000d, cKm * 1000d);
                csv.AppendLine(FormattableString.Invariant($"{p.Lake},{(bakeLakes[p.Lake].Closed ? 1 : 0)},{graph.Lakes[p.Lake].Level:F0},{graph.Lakes[p.Lake].CellCount},{aKm:F1},{cKm:F1},{lowest[p.Cell]:F1},{p.Max:F1},{p.Count},{p.Below},{site},{distance:F0},{influence:F0}"));
            }
            File.WriteAllText(Path.Combine(dir, "barrier_lakes.csv"), csv.ToString());
        }
        {
            // Stems with a node within one cell of a barrier cell, and the cut depth there against the rest.
            var flaggedPoints = new List<DrainagePoint>(); var flaggedBed = new List<double>();
            var basePoints = new List<DrainagePoint>(); var baseBed = new List<double>();
            var perStem = new List<(int Stem, double Meters, double AroundKm, double AcrossKm, int Site, double Clearance)>();
            int nodesInBarrier = 0, flaggedNodes = 0, closeNodes = 0;
            double flaggedMeters = 0, minClearance = double.PositiveInfinity; int minStem = -1, minSite = -1;
            long nodeCounter = 0;
            for (int i = 0; i < stemCount; i++)
            {
                RiverStem st = net.Stems[i];
                RiverStemCurve curve = net.Curves[i];
                double inLakeEnd = i < net.InLakeEnd.Length ? net.InLakeEnd[i] : 0d;
                double meters = 0, bestClearance = double.PositiveInfinity, bestA = 0, bestC = 0; int bestSite = -1;
                for (int k = 0; k < st.NodeCount; k++, nodeCounter++)
                {
                    double a = net.NodeAroundDm[st.NodeStart + k] / 10d, c = net.NodeAcrossDm[st.NodeStart + k] / 10d;
                    int ringCol = Math.Min((int)(a / aroundStep), ringColumns - 1), row = Math.Clamp((int)Math.Floor(c / acrossStep), 0, rows - 1);
                    bool flagged = false;
                    for (int dy = -1; dy <= 1 && !flagged; dy++)
                        for (int dx = -1; dx <= 1 && !flagged; dx++)
                        {
                            int cell = FieldCell((ringCol + dx + ringColumns) % ringColumns, row + dy);
                            if (cell >= 0 && kind[cell] == DrainageCellKind.Barrier) flagged = true;
                        }
                    int own = FieldCell(ringCol, row);
                    if (own >= 0 && kind[own] == DrainageCellKind.Barrier) nodesInBarrier++;
                    (int site, double distance, double influence) = NearestSite(a, c);
                    double clearance = site >= 0 ? distance - influence : double.PositiveInfinity;
                    if (clearance < minClearance) { minClearance = clearance; minStem = i; minSite = site; }
                    if (clearance < RiverSpatialIndex.ReachRadiusMeters) closeNodes++;
                    double s = Math.Min(curve.Length, curve.S0[k] + 0.5 * curve.Len[k]);
                    bool measure = s > inLakeEnd && (flagged || nodeCounter % 16 == 0);
                    if (flagged)
                    {
                        flaggedNodes++; meters += curve.Len[k];
                        if (clearance < bestClearance) { bestClearance = clearance; bestA = a; bestC = c; bestSite = site; }
                    }
                    if (!measure) continue;
                    double level = RiverLevel.AtStem(net, i, s);
                    if (double.IsNaN(level)) continue;
                    net.ChannelAt(i, s, out RiverChannel channel);
                    net.Centre(i, s, out double ca, out double cc, out _, out _);
                    var point = new DrainagePoint((long)Math.Floor(net.WrapAround(ca) + 0.5) % circumference, Math.Clamp((int)Math.Floor(cc + 0.5), 0, acrossMax));
                    if (flagged) { flaggedPoints.Add(point); flaggedBed.Add(level - channel.Depth); } else { basePoints.Add(point); baseBed.Add(level - channel.Depth); }
                }
                if (meters > 0) { perStem.Add((i, meters, bestA / 1000d, bestC / 1000d, bestSite, bestClearance)); flaggedMeters += meters; }
            }
            string Cuts(List<DrainagePoint> points, List<double> bed)
            {
                if (points.Count == 0) return "-";
                var h = new float[points.Count];
                source.SamplePoints(points.ToArray(), h);
                var cuts = new List<double>(points.Count);
                for (int i = 0; i < h.Length; i++) cuts.Add(h[i] - bed[i]);
                cuts.Sort();
                return FormattableString.Invariant($"p50 {Quantile(cuts, 0.5):F1} m, p90 {Quantile(cuts, 0.9):F1} m, p99 {Quantile(cuts, 0.99):F1} m, max {Quantile(cuts, 1):F1} m ({cuts.Count} nodes)");
            }
            sb.AppendLine(FormattableString.Invariant($"  stems with a node within one cell of a barrier cell: {perStem.Count} of {stemCount}, {flaggedNodes} nodes, {flaggedMeters / 1000d:F0} km; cut depth (natural centre ground minus bed) at those nodes: {Cuts(flaggedPoints, flaggedBed)}; at every 16th other node: {Cuts(basePoints, baseBed)}"));
            sb.AppendLine(FormattableString.Invariant($"  stem nodes inside a barrier cell: {nodesInBarrier} (must be 0); smallest clearance between a stem node and an emitter site's influence disc: {minClearance:F0} m (stem {minStem}, site {minSite}); nodes closer than the 650 m reach radius: {closeNodes}"));
            sb.AppendLine("  the five stems with the most length beside barrier cells:");
            foreach (var p in perStem.OrderByDescending(p => p.Meters).ThenBy(p => p.Stem).Take(5))
                sb.AppendLine(FormattableString.Invariant($"  stem {p.Stem}: {p.Meters / 1000d:F1} km beside barrier cells; closest node (around {p.AroundKm:F3} km, across {p.AcrossKm:F3} km) {p.Clearance:F0} m outside the influence disc of site {p.Site}"));
            if (hasNear)
            {
                sb.AppendLine("  inside the --near disc, the five with the most length:");
                foreach (var p in perStem.Where(p => IsNear(p.AroundKm, p.AcrossKm)).OrderByDescending(p => p.Meters).ThenBy(p => p.Stem).Take(5))
                    sb.AppendLine(FormattableString.Invariant($"  stem {p.Stem}: {p.Meters / 1000d:F1} km beside barrier cells; closest node (around {p.AroundKm:F3} km, across {p.AcrossKm:F3} km) {p.Clearance:F0} m outside the influence disc of site {p.Site}, w {2d * table.HalfWidth[LastSpan(p.Stem)]:F1} m at its end"));
            }
        }
        {
            // Lakes touching the rim rows.
            var rim = new List<(int Lake, int Cells, int Col, int Row)>();
            for (int lake = 0; lake < lakeCount; lake++)
            {
                RiverLake l = graph.Lakes[lake];
                if (l.CellCount == 0 || (l.MinRow > 0 && l.MaxRow < rows - 1)) continue;
                int cells = 0, col0 = -1, row0 = -1;
                foreach ((int col, int row) in LakeCells(lake))
                    if (row == 0 || row == rows - 1) { cells++; if (col0 < 0) { col0 = col; row0 = row; } }
                if (cells > 0) rim.Add((lake, cells, col0, row0));
            }
            sb.AppendLine(FormattableString.Invariant($"  lakes touching row 0 or row {rows - 1}: {rim.Count} ({rim.Sum(r => r.Cells)} flooded cells in those rows)"));
            IEnumerable<(int Lake, int Cells, int Col, int Row)> pick = rim.OrderByDescending(r => r.Cells).ThenBy(r => r.Lake).Take(5);
            if (hasNear) pick = pick.Concat(rim.Where(r => IsNear((r.Col + 0.5) * aroundStep / 1000d, (r.Row + 0.5) * acrossStep / 1000d)).OrderByDescending(r => r.Cells).Take(5));
            foreach (var r in pick)
            {
                double aKm = (r.Col + 0.5) * aroundStep / 1000d, cKm = (r.Row + 0.5) * acrossStep / 1000d;
                sb.AppendLine(FormattableString.Invariant($"  lake {r.Lake} level {graph.Lakes[r.Lake].Level:F0} m, {graph.Lakes[r.Lake].CellCount} cells, {r.Cells} in a rim row; first rim cell (around {aKm:F1} km, across {cKm:F1} km){(IsNear(aKm, cKm) ? " [near]" : "")}"));
            }
        }
        sb.AppendLine(FormattableString.Invariant($"barrier section {clock.Elapsed.TotalSeconds:F1} s"));
        return sb.ToString();
    }
}
