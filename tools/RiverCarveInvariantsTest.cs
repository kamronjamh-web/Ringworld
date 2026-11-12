using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-7 (docs/work-orders/terrain-vegetation/T2.md, S7): the river operator in the height model. A seed-1337 recipe with water.rivers, a 400 km CPU strip graph handed to
/// the model directly, 200 reaches and 50 confluences picked by hash, cross-sections every 25 m sampled at 0.25 m (the 0.5 m checks use every second sample). Each check
/// holds at 99.5 % with a hard limit: continuity, containment, level monotonicity and adjacent wet levels, cut and fill, footprint, corridor weight, and the site registry identity.
/// Containment (T3R-3 escalation decision, T3.md Review notes) is judged on wet-dry pairs whose dry sample lies outside every channel; where the dry sample is a channel's own bank
/// (at or above its own level) the two levels must differ by at most 0.6 m instead, because the difference there is the fall of the water surface, not a leak.
/// Negative controls: an identity stand-in (no water, must pass), a stand-in with a 2 m step at the corridor edge (must fail), and the core lower bound switched off with a fabricated
/// dip in the bank zone (containment must fail). Prints the sample cost near rivers and elsewhere. Set RIVER_CARVE_PICTURES to a folder to also write the before/after relief crops.
/// Prints RIVER_CARVE_INVARIANTS=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests river_carve_invariants_test.
/// </summary>
public partial class RiverCarveInvariantsTest : RefCounted
{
    private const long Seed = 1337;
    private const int StripKilometres = 400;
    private const double StepMeters = 0.25;
    /// <summary>
    /// Tuned in the T2R-7 review (was 0.5): a tripwire for this indicative, one-thread, dev-build number (measured 2.0 to 2.4 us with the machine in use, against 2.9 to
    /// 3.3 us for the natural sample on the same points). docs/work-orders/terrain-vegetation/T2.md, T2R-7b, has the plan and the target of 1.0 us.
    /// </summary>
    private const double NearCostGateMicroseconds = 3.0;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string message) => Console.WriteLine("RIVER_CARVE_INVARIANTS_DETAIL " + message);

    private readonly record struct Sample(double Height, InlandWaterKind Kind, double Level, double Weight, double Delta)
    {
        public bool Wet => (Kind == InlandWaterKind.River || Kind == InlandWaterKind.Lake) && Height < Level;
    }

    private static RiverNetwork? _netStatic;

    private delegate Sample Eval(double around, double across, double footprint);

    private sealed class Section
    {
        public int Stem;
        public double S, Around, Across, Nx, Ny, Tx, Ty, A, B, Extent;
        /// <summary>A lake, its ring or the cell next to the ring lies within a cell of the section (a stem ending in or leaving a lake, a river beside one): the channel there meets the lake operator (T2R-8), so its bank fill is counted apart.</summary>
        public bool LakeTerminal;
        /// <summary>Taken at a confluence (on the receiving stem around the junction, or on the tributary's last 150 m): two channels closer than a coarse lattice can resolve.</summary>
        public bool Confluence;
        /// <summary>Added by the lake-end picks (T2R-8); the fabricated-dip control leaves them out.</summary>
        public bool LakeEnd;
    }

    private sealed class Stats
    {
        public long LakeBankSamples, LakeFillOver3;
        public double LakeFillMax;
        public long Sections, ContPairs, ContOver, WetSamples, ContainPairs, ContainBad, LevelPairs, LevelBad, BankSamples, FillOver3, WeightSamples, WeightBad;
        public double ContMax, ContainMax, LevelStepMax, FillMax;
        /// <summary>
        /// The wet-dry pairs split by the dry sample (T3R-3 escalation decision): Leak = it lies outside every channel, so ground below the wet level is a leak (limits 0.05 m at 99.5 %,
        /// hard 0.5 m); Waterline = it is a channel's own bank at or above its own level, so the difference is the fall of the water surface over 2 m (limit 0.6 m at 99.5 %, hard 1 m).
        /// Contain* keeps counting every pair against the wet level, printed for the trend only.
        /// </summary>
        public long LeakPairs, LeakBad, WaterlinePairs, WaterlineBad;
        public double LeakMax, WaterlineMax;
        /// <summary>The first few failing samples of each kind, printed so a failure can be read without a rerun.</summary>
        public readonly List<string> Examples = new();
        public void Example(string text) { if (Examples.Count < 40) Examples.Add(text); }
        /// <summary>Bank samples over 3 m of fill by section (held or main, stem, s), for reading where the fill is.</summary>
        public readonly Dictionary<string, long> FillTally = new();
        public void Merge(Stats o)
        {
            foreach (KeyValuePair<string, long> kv in o.FillTally) FillTally[kv.Key] = FillTally.GetValueOrDefault(kv.Key) + kv.Value;
            LakeBankSamples += o.LakeBankSamples; LakeFillOver3 += o.LakeFillOver3; LakeFillMax = Math.Max(LakeFillMax, o.LakeFillMax);
            Sections += o.Sections; ContPairs += o.ContPairs; ContOver += o.ContOver; WetSamples += o.WetSamples; ContainPairs += o.ContainPairs; ContainBad += o.ContainBad;
            LevelPairs += o.LevelPairs; LevelBad += o.LevelBad; BankSamples += o.BankSamples; FillOver3 += o.FillOver3; WeightSamples += o.WeightSamples; WeightBad += o.WeightBad;
            LeakPairs += o.LeakPairs; LeakBad += o.LeakBad; WaterlinePairs += o.WaterlinePairs; WaterlineBad += o.WaterlineBad;
            LeakMax = Math.Max(LeakMax, o.LeakMax); WaterlineMax = Math.Max(WaterlineMax, o.WaterlineMax);
            foreach (string e in o.Examples) Example(e);
            ContMax = Math.Max(ContMax, o.ContMax); ContainMax = Math.Max(ContainMax, o.ContainMax); LevelStepMax = Math.Max(LevelStepMax, o.LevelStepMax); FillMax = Math.Max(FillMax, o.FillMax);
        }
    }

    public int Run()
    {
        try
        {
            Build();
            Sites();
            StandIns();
            Sections();
            Lakes();
            LakeWater();
            Paths();
            MeanderSlope();
            Footprints();
            FarPoints();
            Cost();
            Pictures();
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_CARVE_INVARIANTS=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_CARVE_INVARIANTS=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("RIVER_CARVE_INVARIANTS=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixture

    private TerrainGenerationParameters _recipe = null!;
    private RiverGraph _graph = null!;
    private RiverNetwork _net = null!;
    private DrainageGridSpec _spec = null!;
    private int _workers;
    private readonly List<Section> _sections = new();
    private readonly List<int> _reaches = new(), _confluences = new();
    private double _stripAroundMin, _stripAroundMax;

    private TerrainGenerationHeightModel NewModel() => new(_recipe, true, _graph);

    private void Build()
    {
        _recipe = EmitterSiteTestWorlds.Gen17WorldRecipe((int)Seed, 1, new[] { "water.rivers" });
        Check(_recipe.HasRivers, "recipe lacks water.rivers");
        _workers = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6);
        TerrainGenerationParameters plain = DrainageGridTest.Recipe();
        using RingTerrainSampler root = DrainageGridTest.CreateSampler(plain);
        DrainageClimateInputs climate = DrainageClimateInputs.FromSampler(root);
        var source = new CpuDrainageHeightSource(_recipe, climate, _workers);
        _spec = DrainageGridSpec.Strip(_recipe, 9_804_000d, StripKilometres);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrainageGrid grid = source.SampleGrid(_spec, null, CancellationToken.None);
        DrainageField field = DrainageBake.Bake(grid, _recipe, _workers, null, CancellationToken.None);
        _net = RiverGraphBuilder.Build(field, grid, _recipe, new RiverBuildOptions { Source = source, Workers = _workers }, CancellationToken.None, out RiverBuildReport report);
        _graph = RiverGraph.Build(_net, field, grid, 0x1234UL, 0);
        _netStatic = _net;
        Detail($"strip graph: {report.Stems} stems, {report.Reaches} reaches, {report.Nodes} nodes, built in {clock.Elapsed.TotalSeconds:F1} s with {_workers} workers");
        _stripAroundMin = _spec.ColumnStart * _spec.AroundStepMeters; _stripAroundMax = _stripAroundMin + _spec.Columns * _spec.AroundStepMeters;

        // Deterministic picks: by hash of (seed, index), 200 reaches and 50 confluences, away from the strip ends and the rim, on ground at least 2 m.
        using TerrainGenerationHeightModel natural = new(_recipe, false);
        bool Usable(int stem, double s)
        {
            _net.Centre(stem, s, out double around, out double across, out _, out _);
            return around > _stripAroundMin + 20_000 && around < _stripAroundMax - 20_000 && across > 3000 && across < _net.WidthM - 3000 && natural.CalculateHeight(around, across) >= 2d;
        }
        foreach (int r in Enumerable.Range(0, _net.Reaches.Length).OrderBy(r => RiverHash.Mix(Seed, r, 71)))
        {
            RiverReach reach = _net.Reaches[r];
            if (reach.SEnd - reach.SStart < 250f || !Usable(reach.Stem, 0.5 * (reach.SStart + reach.SEnd))) continue;
            _reaches.Add(r);
            if (_reaches.Count == 200) break;
        }
        foreach (int i in Enumerable.Range(0, _net.Stems.Length).Where(i => _net.Stems[i].Terminal == RiverTerminal.Confluence).OrderBy(i => RiverHash.Mix(Seed, i, 72)))
        {
            RiverStem st = _net.Stems[i];
            if (_net.Curves[i].Length < 200 || !Usable(st.TerminalId, st.JunctionS) || !Usable(i, _net.Curves[i].Length - 100)) continue;
            _confluences.Add(i);
            if (_confluences.Count == 50) break;
        }
        Check(_reaches.Count >= 150 && _confluences.Count >= 20, $"too few picks: {_reaches.Count} reaches, {_confluences.Count} confluences");
        foreach (int r in _reaches)
        {
            RiverReach reach = _net.Reaches[r];
            double mid = 0.5 * (reach.SStart + reach.SEnd), s0 = Math.Max(reach.SStart, mid - 100);
            for (int k = 0; k < 8 && s0 + 25 * k <= reach.SEnd; k++) TryAddSection(natural, reach.Stem, s0 + 25 * k, false);
        }
        foreach (int i in _confluences)
        {
            RiverStem st = _net.Stems[i];
            for (double s = st.JunctionS - 100; s <= st.JunctionS + 100; s += 25) TryAddSection(natural, st.TerminalId, s, true);
            for (double s = _net.Curves[i].Length - 150; s <= _net.Curves[i].Length; s += 25) TryAddSection(natural, i, s, true);
        }
        // Lake ends (T2R-8): the last 250 m of up to 60 stems that end in a lake and the first 250 m of stems that leave one, where the channel is held at the lake level.
        int lakeEnds = 0;
        foreach (int i in Enumerable.Range(0, _net.Stems.Length).Where(i => _net.Stems[i].Terminal == RiverTerminal.Lake || _net.Reaches[_net.Stems[i].ReachStart].UpstreamLake >= 0).OrderBy(i => RiverHash.Mix(Seed, i, 73)))
        {
            double length = _net.Curves[i].Length;
            bool inlet = _net.Stems[i].Terminal == RiverTerminal.Lake;
            if (length < 400 || !Usable(i, inlet ? length - 100 : 100)) continue;
            for (double s = inlet ? length - 250 : 0; s <= (inlet ? length : 250); s += 25) TryAddSection(natural, i, s, false, true);
            if (++lakeEnds == 60) break;
        }
        Detail($"lake-end stems added: {lakeEnds}");
        Detail($"confluence stems on the strip: {_net.Stems.Count(x => x.Terminal == RiverTerminal.Confluence)} (the 400 km strip has fewer than 50 usable ones)");
        Detail($"picks: {_reaches.Count} reaches, {_confluences.Count} confluences, {_sections.Count} cross-sections");
    }

    /// <summary>A lake, its ring or a cell next to the ring lies within a cell of the point: the section meets the lake operator.</summary>
    private bool LakeNear(double around, double across)
    {
        int col = _graph.Index.ColumnOf(around), row = _graph.Index.RowOf(across);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int c = (col + dx + _graph.RingColumns) % _graph.RingColumns, r = row + dy;
                if ((uint)r < (uint)_graph.Rows && (_graph.Index.IsFlooded(c, r) || _graph.Index.IsRing(c, r))) return true;
            }
        return false;
    }

    private void TryAddSection(TerrainGenerationHeightModel natural, int stem, double s, bool confluence, bool lakeEnd = false)
    {
        s = Math.Clamp(s, 0d, _net.Curves[stem].Length);
        _net.CentreFrame(stem, s, out double around, out double across, out double nx, out double ny);
        if (natural.CalculateHeight(around, across) < 2d) return;
        _net.ChannelAt(stem, s, out RiverChannel channel);
        double a = channel.HalfWidth, b = channel.BankWidth, f = channel.Floodplain;
        _sections.Add(new Section { Confluence = confluence, LakeEnd = lakeEnd, LakeTerminal = LakeNear(around, across), Stem = stem, S = s, Around = around, Across = across, Nx = nx, Ny = ny, Tx = ny, Ty = -nx, A = a, B = b, Extent = Math.Min(300d, a + b + f + 120d) });
    }

    // ------------------------------------------------------------------ the checker

    private static Sample Real(TerrainGenerationHeightModel m, double around, double across, double fp)
    {
        TerrainGenerationHeightModel.Diagnostics d = m.SampleDiagnostics(around, across, fp);
        return new Sample(d.Height, d.InlandWater, d.WaterLevel, d.RiverCorridorWeight, d.RiverGroundDelta);
    }

    private static Sample Identity(TerrainGenerationHeightModel m, double around, double across, double fp)
        => new(m.SampleNaturalDiagnostics(around, across).Height, InlandWaterKind.None, double.NaN, 0d, 0d);

    /// <summary>Runs the section checks on one cross-section line sampled every 0.25 m.</summary>
    private static void CheckSection(Section sec, Eval eval, Stats st, Sample[] buffer)
    {
        int n = (int)Math.Round(2 * sec.Extent / StepMeters) + 1;
        double Around(double x) => sec.Around + x * sec.Nx;
        double Across(double x) => sec.Across + x * sec.Ny;
        st.Sections++;
        for (int j = 0; j < n; j++) { double x = -sec.Extent + j * StepMeters; buffer[j] = eval(Around(x), Across(x), 0d); }
        for (int j = 0; j + 1 < n; j++)
        {
            double step = Math.Abs(buffer[j + 1].Height - buffer[j].Height);
            st.ContPairs++; if (step > 0.6) st.ContOver++;
            if (step > 1.5) st.Example(FormattableString.Invariant($"continuity {step:F2} m: stem {sec.Stem} s {sec.S:F0} x {-sec.Extent + j * StepMeters:F2} (a {sec.A:F1}, b {sec.B:F1}) heights {buffer[j].Height:F2} -> {buffer[j + 1].Height:F2}, kinds {buffer[j].Kind}/{buffer[j + 1].Kind}, delta {buffer[j].Delta:F1}/{buffer[j + 1].Delta:F1}"));
            if (step > st.ContMax) st.ContMax = step;
            double w0 = buffer[j].Weight, w1 = buffer[j + 1].Weight;
            st.WeightSamples++;
            if (w0 < -1e-9 || w0 > 1 + 1e-9 || Math.Abs(w1 - w0) > 0.02) st.WeightBad++;
            if (buffer[j].Kind == InlandWaterKind.River && w0 < 1 - 1e-9) st.WeightBad++;
        }
        int centre = (int)Math.Round(sec.Extent / StepMeters);
        if (buffer[centre].Kind == InlandWaterKind.River && buffer[centre].Weight < 1 - 1e-9) st.WeightBad++;
        for (int j = 0; j < n; j += 2)
        {
            double x = -sec.Extent + j * StepMeters, ax = Math.Abs(x);
            Sample p = buffer[j];
            if (ax > sec.A && ax <= sec.A + sec.B + 20 && sec.LakeTerminal)
            {
                st.LakeBankSamples++;
                if (p.Delta > 3) { st.LakeFillOver3++; { string key = $"held stem {sec.Stem} ({_netStatic?.Stems[sec.Stem].Terminal}, length {_netStatic?.Curves[sec.Stem].Length:F0}) s {sec.S:F0}"; st.FillTally[key] = st.FillTally.GetValueOrDefault(key) + 1; } if (p.Delta > 15 && ax < sec.A + 1d) st.Example(FormattableString.Invariant($"lake-held fill {p.Delta:F1} m: stem {sec.Stem} s {sec.S:F0} of {_netStatic?.Curves[sec.Stem].Length:F0} x {x:F2} height {p.Height:F2} kind {p.Kind} level {p.Level:F2} weight {p.Weight:F2}")); }
                if (p.Delta > st.LakeFillMax) st.LakeFillMax = p.Delta;
            }
            if (ax > sec.A && ax <= sec.A + sec.B + 20 && !sec.LakeTerminal)
            {
                st.BankSamples++;
                if (p.Delta > 3) { st.FillOver3++; string key = $"main stem {sec.Stem} ({_netStatic?.Stems[sec.Stem].Terminal}, length {_netStatic?.Curves[sec.Stem].Length:F0}) s {sec.S:F0}"; st.FillTally[key] = st.FillTally.GetValueOrDefault(key) + 1; }
                if (p.Delta > 15 && ax < sec.A + 1d) st.Example(FormattableString.Invariant($"fill {p.Delta:F1} m: stem {sec.Stem} ({_netStatic?.Stems[sec.Stem].Terminal}) s {sec.S:F0} of {_netStatic?.Curves[sec.Stem].Length:F0} x {x:F2} height {p.Height:F2} kind {p.Kind} level {p.Level:F2}"));
                if (p.Delta > st.FillMax) st.FillMax = p.Delta;
            }
            if (j + 2 < n && p.Wet && buffer[j + 2].Wet)
            {
                st.LevelPairs++;
                double step = Math.Abs(p.Level - buffer[j + 2].Level);
                if (step > 0.6) st.LevelBad++;
                if (step > st.LevelStepMax) st.LevelStepMax = step;
            }
            if (!p.Wet) continue;
            st.WetSamples++;
            for (int dir = 0; dir < 4; dir++)
            {
                Sample q;
                if (dir < 2) { int jq = j + (dir == 0 ? 8 : -8); if (jq < 0 || jq >= n) continue; q = buffer[jq]; }
                else { double sgn = dir == 2 ? 2d : -2d; q = eval(Around(x) + sgn * sec.Tx, Across(x) + sgn * sec.Ty, 0d); }
                if (q.Wet) continue;
                st.ContainPairs++;
                double violation = p.Level - q.Height;
                if (violation > 0.05) st.ContainBad++;
                if (violation > st.ContainMax) st.ContainMax = violation;
                if (q.Kind == InlandWaterKind.River)
                {
                    // The dry sample is a channel's own bank, at or above its own level (it is not wet): nothing can leak there. What can be wrong is a step in the water surface.
                    double fall = Math.Abs(p.Level - q.Level);
                    st.WaterlinePairs++;
                    if (fall > 0.6) st.WaterlineBad++;
                    if (fall > 1d) st.Example(FormattableString.Invariant($"waterline step {fall:F2} m: stem {sec.Stem} s {sec.S:F0} x {x:F2} dir {dir} (a {sec.A:F1}, b {sec.B:F1}) wet height {p.Height:F2} level {p.Level:F2}; dry height {q.Height:F2} level {q.Level:F2}"));
                    if (fall > st.WaterlineMax) st.WaterlineMax = fall;
                    continue;
                }
                st.LeakPairs++;
                if (violation > 0.05) st.LeakBad++;
                if (violation > 0.5) st.Example(FormattableString.Invariant($"containment {violation:F2} m: stem {sec.Stem} s {sec.S:F0} x {x:F2} dir {dir} (a {sec.A:F1}, b {sec.B:F1}) wet height {p.Height:F2} level {p.Level:F2}; dry height {q.Height:F2} kind {q.Kind} level {q.Level:F2} delta {q.Delta:F1}"));
                if (violation > st.LeakMax) st.LeakMax = violation;
            }
        }
    }

    private Stats RunSections(Func<TerrainGenerationHeightModel, Section, Eval> make, IReadOnlyList<Section> sections)
    {
        var total = new Stats();
        var locker = new object();
        int bufferLength = (int)Math.Round(2 * 300d / StepMeters) + 2;
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel model = NewModel();
            var local = new Stats();
            var buffer = new Sample[bufferLength];
            for (int i = w; i < sections.Count; i += _workers) CheckSection(sections[i], make(model, sections[i]), local, buffer);
            lock (locker) total.Merge(local);
        });
        return total;
    }

    /// <summary>The section checks with a bare operator on the natural ground (no sites), for comparing operator variants on the same sections.</summary>
    private Stats RunSectionsOp(RiverCarveOperator op, IReadOnlyList<Section> sections)
    {
        var total = new Stats();
        var locker = new object();
        int bufferLength = (int)Math.Round(2 * 300d / StepMeters) + 2;
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel natural = new(_recipe, false);
            var local = new Stats();
            var buffer = new Sample[bufferLength];
            Eval eval = (a, c, fp) => { RiverOperatorResult r = op.Evaluate(a, c, natural.CalculateHeight(a, c), fp); return new Sample(r.Height, r.Kind, r.WaterLevel, r.CorridorWeight, r.GroundDelta); };
            for (int i = w; i < sections.Count; i += _workers) CheckSection(sections[i], eval, local, buffer);
            lock (locker) total.Merge(local);
        });
        return total;
    }

    private static string Text(string label, Stats s)
        => FormattableString.Invariant($"{label}: {s.Sections} sections, continuity {s.ContOver}/{s.ContPairs} over 0.6 m (max {s.ContMax:F2} m), containment {s.LeakBad}/{s.LeakPairs} wet-dry pairs with ground outside every channel below the level (largest {s.LeakMax:F2} m), waterline levels {s.WaterlineBad}/{s.WaterlinePairs} wet-bank pairs over 0.6 m (largest {s.WaterlineMax:F2} m), all wet-dry pairs against the wet level {s.ContainBad}/{s.ContainPairs} over 0.05 m (largest {s.ContainMax:F2} m; for the trend, not a check), wet samples {s.WetSamples}, adjacent wet levels {s.LevelBad}/{s.LevelPairs} over 0.6 m (max {s.LevelStepMax:F2} m), fill over 3 m on {s.FillOver3}/{s.BankSamples} bank samples (max {s.FillMax:F1} m), corridor weight {s.WeightBad}/{s.WeightSamples} bad");

    // ------------------------------------------------------------------ negative controls

    private void StandIns()
    {
        List<Section> subset = _sections.Where((_, i) => i % 12 == 0).ToList();
        // Permanent stand-in 1: the identity operator (no water, continuity passes). The checker must not fail it.
        Stats identity = RunSections((m, sec) => (a, c, fp) => Identity(m, a, c, fp), subset);
        Detail(Text("stand-in identity", identity));
        Check(identity.ContMax <= 2d && identity.ContOver <= identity.ContPairs * 0.005 && identity.WetSamples == 0 && identity.ContainBad == 0, "the identity stand-in failed the checker");
        // Permanent stand-in 2: a 2 m step at the corridor edge. Continuity must fail (hard limit 2 m).
        Stats broken = RunSections((m, sec) => (a, c, fp) =>
        {
            Sample s = Real(m, a, c, fp);
            return s with { Height = s.Height + (s.Weight < 0.5 ? 2d : 0d) };
        }, subset);
        Detail(Text("stand-in broken step", broken));
        Check(broken.ContMax > 2d, $"negative control: the 2 m step at the corridor edge was not flagged (largest step {broken.ContMax:F2} m)");
        // Core lower bound off, with a fabricated dip below the level across the bank zone and beyond: the dip becomes wet next to dry ground below the level.
        var noCore = new RiverCarveOperator(_graph) { CoreLowerBound = false };
        // Reach sections only (T2R-7 review): the dip is cut relative to the level at the section's centre, which is not the level field around a confluence, where the
        // operator draws a higher channel down to a lower one.
        var dipSections = _sections.Where(x => !x.Confluence && !x.LakeEnd).Where((_, i) => i % 5 == 0).ToList();
        Stats Dip(RiverCarveOperator op)
        {
            var total = new Stats();
            var locker = new object();
            int bufferLength = (int)Math.Round(2 * 300d / StepMeters) + 2;
            Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
            {
                using TerrainGenerationHeightModel model = new(_recipe, false);
                var local = new Stats();
                var buffer = new Sample[bufferLength];
                for (int i = w; i < dipSections.Count; i += _workers)
                {
                    Section sec = dipSections[i];
                    RiverOperatorResult centre = op.Evaluate(sec.Around, sec.Across, model.CalculateHeight(sec.Around, sec.Across), 0d);
                    if (centre.Kind != InlandWaterKind.River || !(centre.WaterLevel >= 5d)) continue;
                    double level = centre.WaterLevel;
                    CheckSection(sec, (a, c, fp) =>
                    {
                        double x = (a - sec.Around) * sec.Nx + (c - sec.Across) * sec.Ny, g = model.CalculateHeight(a, c);
                        if (Math.Abs(x) >= sec.A + sec.B - 2d && Math.Abs(x) <= sec.A + sec.B + 20d) g = Math.Min(g, level - 3d);
                        RiverOperatorResult r = op.Evaluate(a, c, g, fp);
                        return new Sample(r.Height, r.Kind, r.WaterLevel, r.CorridorWeight, r.GroundDelta);
                    }, local, buffer);
                }
                lock (locker) total.Merge(local);
            });
            return total;
        }
        Stats withCore = Dip(new RiverCarveOperator(_graph)), withoutCore = Dip(noCore);
        Detail(Text("fabricated dip, core lower bound on", withCore));
        Detail(Text("fabricated dip, core lower bound OFF", withoutCore));
        Check(withoutCore.LeakBad > withoutCore.LeakPairs * 0.005 && withoutCore.LeakMax > 0.5, "negative control: containment did not fail with the core lower bound off on a fabricated dip");
        Check(withCore.LeakBad <= withCore.LeakPairs * 0.005 && withCore.LeakMax <= 0.5, "the fabricated dip leaked with the core lower bound on");
    }

    // ------------------------------------------------------------------ the real operator

    private void Sections()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // The lake-end picks (T2R-8) are judged apart below: they sit where the channel meets a lake and exposed steps that are present with the lake operator off as well.
        Stats real = RunSections((m, sec) => (a, c, fp) => Real(m, a, c, fp), _sections.Where(x => !x.LakeEnd).ToList());
        Detail(Text("real operator", real) + FormattableString.Invariant($"; {clock.Elapsed.TotalSeconds:F1} s"));
        foreach (string e in real.Examples) Detail("example " + e);
        foreach (KeyValuePair<string, long> kv in real.FillTally.OrderByDescending(k => k.Value).Take(14)) Detail($"fill over 3 m: {kv.Key}: {kv.Value} samples");
        Check(real.Sections > 1000 && real.WetSamples > 10000, $"too little wet ground sampled ({real.Sections} sections, {real.WetSamples} wet samples)");
        Check(real.ContOver <= real.ContPairs * 0.005 && real.ContMax <= 2d, $"continuity: {real.ContOver}/{real.ContPairs} steps over 0.6 m, largest {real.ContMax:F2} m");
        Check(real.LeakBad <= real.LeakPairs * 0.005 && real.LeakMax <= 0.5, $"containment: {real.LeakBad}/{real.LeakPairs} pairs, largest violation {real.LeakMax:F2} m");
        Check(real.WaterlineBad <= real.WaterlinePairs * 0.005 && real.WaterlineMax <= 1d, $"waterline levels: {real.WaterlineBad}/{real.WaterlinePairs} wet-bank pairs differ by more than 0.6 m, largest {real.WaterlineMax:F2} m");
        Check(real.LevelBad <= real.LevelPairs * 0.005, $"adjacent wet levels: {real.LevelBad}/{real.LevelPairs} differ by more than 0.6 m (largest {real.LevelStepMax:F2} m)");
        // Sections that meet a lake (within a cell of a lake or its ring, plus the lake-end picks), natural ground, lake operator on and off. On: containment and fill under the general bars,
        // and no step or waterline difference worse than without lakes. Off (the T2R-7 state): must fail the containment and fill bars, which proves they can.
        List<Section> near = _sections.Where(x => x.LakeTerminal || x.LakeEnd).ToList();
        Stats lakesOff = RunSectionsOp(new RiverCarveOperator(_graph) { LakeOperator = false }, near);
        Stats lakesOn = RunSectionsOp(new RiverCarveOperator(_graph), near);
        Detail(Text("lake-near sections, lake operator off", lakesOff));
        Detail(Text("lake-near sections, lake operator on", lakesOn));
        foreach (string e in lakesOn.Examples.Where(e => e.StartsWith("continuity") || e.StartsWith("containment") || e.StartsWith("waterline"))) Detail("example (on) " + e);
        foreach (string e in lakesOff.Examples.Where(e => e.StartsWith("continuity"))) Detail("example (off) " + e);
        foreach (KeyValuePair<string, long> kv in lakesOn.FillTally.OrderByDescending(k => k.Value).Take(8)) Detail($"lake-near fill over 3 m (on): {kv.Key}: {kv.Value} samples");
        Detail(FormattableString.Invariant($"lake-near bank fill over 3 m: lake operator off {lakesOff.LakeFillOver3}/{lakesOff.LakeBankSamples} (largest {lakesOff.LakeFillMax:F1} m), on {lakesOn.LakeFillOver3}/{lakesOn.LakeBankSamples} ({(double)lakesOn.LakeFillOver3 / Math.Max(1, lakesOn.LakeBankSamples):P2}, largest {lakesOn.LakeFillMax:F1} m); before T2R-8 the lake-terminal stems showed up to 31.9 m and were excluded"));
        Check(lakesOn.LakeBankSamples > 20000 && lakesOn.WetSamples > 10000, $"too little lake-near ground sampled ({lakesOn.LakeBankSamples} bank samples, {lakesOn.WetSamples} wet)");
        Check(lakesOn.LeakBad <= lakesOn.LeakPairs * 0.005 && lakesOn.LeakMax <= 0.5, $"lake-near containment: {lakesOn.LeakBad}/{lakesOn.LeakPairs} pairs, largest violation {lakesOn.LeakMax:F2} m");
        Check(lakesOn.LakeFillOver3 <= lakesOn.LakeBankSamples * 0.02 && lakesOn.LakeFillMax <= 15d, $"lake-near fill: {lakesOn.LakeFillOver3}/{lakesOn.LakeBankSamples} bank samples over 3 m, largest {lakesOn.LakeFillMax:F1} m");
        Check(lakesOn.ContMax <= lakesOff.ContMax && lakesOn.ContOver <= lakesOff.ContOver && lakesOn.WaterlineBad <= lakesOff.WaterlineBad && lakesOn.WaterlineMax <= lakesOff.WaterlineMax + 1e-9, "the lake operator makes a step or a waterline difference worse than the river-only state");
        Check(lakesOff.ContMax > lakesOn.ContMax && lakesOff.ContOver > lakesOn.ContOver, "negative control: the lake-near step comparison could not tell the lake operator from the river-only state");
        Check(lakesOff.LeakBad > lakesOff.LeakPairs * 0.005 && lakesOff.LeakMax > 0.5, $"negative control: the lake-near containment bar did not fail with the lake operator off ({lakesOff.LeakBad}/{lakesOff.LeakPairs}, largest {lakesOff.LeakMax:F2} m)");
        Check(lakesOff.LakeFillOver3 > lakesOff.LakeBankSamples * 0.02 && lakesOff.LakeFillMax > 15d, $"negative control: the lake-near fill bar did not fail with the lake operator off ({lakesOff.LakeFillOver3}/{lakesOff.LakeBankSamples}, largest {lakesOff.LakeFillMax:F1} m)");
        Check(real.FillOver3 <= real.BankSamples * 0.02 && real.FillMax <= 15d, $"fill: {real.FillOver3}/{real.BankSamples} bank samples over 3 m, largest {real.FillMax:F1} m");
        Check(real.WeightBad <= real.WeightSamples * 0.0005, $"corridor weight: {real.WeightBad}/{real.WeightSamples} outside [0, 1], discontinuous, or below 1 on the water");
    }

    // ------------------------------------------------------------------ lakes (T2R-8, S8)

    private sealed class LakeCase
    {
        public int Lake;
        public List<(int Col, int Row)> Ring = new();
        public List<(int Col, int Row, int Dx, int Dy)> Shore = new();
    }

    private sealed class LakeStats
    {
        public long Samples, WetSamples, WetNoWeight, RingSamples, RingOver5, Plugs, LakeKind, Pairs, LeakPairs, LeakBad, WaterlinePairs, WaterlineBad;
        public double LeakMax, WaterlineMax, PlugMax;
        public readonly List<float> PlugHeights = new();
        public readonly List<string> Examples = new();
        public void Example(string text) { if (Examples.Count < 30) Examples.Add(text); }
        public void Merge(LakeStats o)
        {
            Samples += o.Samples; WetSamples += o.WetSamples; WetNoWeight += o.WetNoWeight; RingSamples += o.RingSamples; RingOver5 += o.RingOver5; Plugs += o.Plugs; LakeKind += o.LakeKind;
            Pairs += o.Pairs; LeakPairs += o.LeakPairs; LeakBad += o.LeakBad; WaterlinePairs += o.WaterlinePairs; WaterlineBad += o.WaterlineBad;
            LeakMax = Math.Max(LeakMax, o.LeakMax); WaterlineMax = Math.Max(WaterlineMax, o.WaterlineMax); PlugMax = Math.Max(PlugMax, o.PlugMax);
            PlugHeights.AddRange(o.PlugHeights);
            foreach (string e in o.Examples) Example(e);
        }
    }

    private List<LakeCase>? _lakeCases;

    /// <summary>The 50 largest lakes of the strip and 50 seeded others, away from the strip ends, with their ring cells and the shore cells (a ring cell and the direction to a flooded neighbour).</summary>
    private List<LakeCase> LakeCases()
    {
        if (_lakeCases != null) return _lakeCases;
        RiverLake[] lakes = _graph.Lakes;
        double step = _graph.AroundStepMeters;
        bool Inside(int lake)
        {
            RiverLake l = lakes[lake];
            double lo = l.MinCol * step, hi = (l.MaxCol + 1) * step;
            return l.CellCount > 0 && lo > _stripAroundMin + 20_000 && hi < _stripAroundMax - 20_000 && l.MinRow > 4 && l.MaxRow < _graph.Rows - 5;
        }
        List<int> usable = Enumerable.Range(0, lakes.Length).Where(Inside).ToList();
        var picked = new List<int>(usable.OrderByDescending(i => lakes[i].CellCount).Take(50));
        picked.AddRange(usable.Except(picked).OrderBy(i => RiverHash.Mix(Seed, i, 81)).Take(50));
        var byLake = picked.ToDictionary(i => i, _ => new List<LakeRun>());
        foreach (LakeRun run in _graph.Runs) if (byLake.TryGetValue(run.LakeId, out List<LakeRun>? list)) list.Add(run);
        var cases = new List<LakeCase>();
        foreach (int id in picked)
        {
            var lc = new LakeCase { Lake = id };
            var seen = new HashSet<long>();
            foreach (LakeRun run in byLake[id])
                for (int c = 0; c < run.Length; c++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int row = run.Row + dy, col = (run.Col + c + dx + _graph.RingColumns) % _graph.RingColumns;
                            if ((dx == 0 && dy == 0) || (uint)row >= (uint)_graph.Rows || !_graph.Index.IsRing(col, row) || !seen.Add((long)row * _graph.RingColumns + col)) continue;
                            lc.Ring.Add((col, row));
                            lc.Shore.Add((col, row, -dx, -dy));
                        }
            if (lc.Ring.Count > 0) cases.Add(lc);
        }
        Check(cases.Count >= 60, $"too few usable lakes on the strip ({cases.Count} of {usable.Count})");
        Detail($"lake cases: {cases.Count} lakes of {usable.Count} usable ({lakes.Length} on the strip), {cases.Sum(c => c.Ring.Count)} ring cells, largest {cases.Max(c => lakes[c.Lake].CellCount)} cells, smallest {cases.Min(c => lakes[c.Lake].CellCount)}");
        return _lakeCases = cases;
    }

    /// <summary>
    /// The lake checks on one evaluator: 100 hashed points on the ring and shore of each lake (weight and plug statistics), and four transects per lake across the ring line
    /// with a sample every 2 m (containment of the lake surface). <paramref name="fabricate"/> may lower the natural ground first (the plug control).
    /// </summary>
    private LakeStats RunLakes(Func<double, double, double, double>? fabricate, Func<Sample, Sample>? tamper, RiverCarveOperator op)
    {
        List<LakeCase> cases = LakeCases();
        var total = new LakeStats();
        var locker = new object();
        double aroundStep = _graph.AroundStepMeters, acrossStep = _graph.AcrossStepMeters;
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel natural = new(_recipe, false);
            var st = new LakeStats();
            Sample Eval(double around, double across)
            {
                double g = natural.CalculateHeight(around, across);
                if (fabricate != null) g = fabricate(around, across, g);
                RiverOperatorResult r = op.Evaluate(around, across, g, 0d);
                Sample sample = new(r.Height, r.Kind, r.WaterLevel, r.CorridorWeight, r.GroundDelta);
                return tamper != null ? tamper(sample) : sample;
            }
            for (int ci = w; ci < cases.Count; ci += _workers)
            {
                LakeCase lc = cases[ci];
                RiverLake lake = _graph.Lakes[lc.Lake];
                for (int k = 0; k < 100; k++)
                {
                    (int col, int row) = lc.Ring[(int)(RiverHash.Unit(Seed, lc.Lake, k, 31) * lc.Ring.Count) % lc.Ring.Count];
                    double around = (col + 0.5 + (RiverHash.Unit(Seed, lc.Lake, k, 32) * 3d - 1.5)) * aroundStep, across = (row + 0.5 + (RiverHash.Unit(Seed, lc.Lake, k, 33) * 3d - 1.5)) * acrossStep;
                    Sample s = Eval(around, across);
                    st.Samples++;
                    if (s.Kind == InlandWaterKind.Lake) st.LakeKind++;
                    if (s.Wet) { st.WetSamples++; if (s.Weight < 1d - 1e-9) { st.WetNoWeight++; st.Example(FormattableString.Invariant($"wet sample with weight {s.Weight:F3}: lake {lc.Lake} at around {around:F0} across {across:F0} kind {s.Kind}")); } }
                    int sc = _graph.Index.ColumnOf(around), sr = _graph.Index.RowOf(across);
                    if (_graph.Index.IsRing(sc, sr))
                    {
                        st.RingSamples++;
                        if (s.Delta > 0.01) { st.Plugs++; st.PlugHeights.Add((float)s.Delta); if (s.Delta > st.PlugMax) st.PlugMax = s.Delta; }
                        if (s.Delta > 5d) { st.RingOver5++; st.Example(FormattableString.Invariant($"plug {s.Delta:F1} m: lake {lc.Lake} (level {lake.Level:F0}) at around {around:F0} across {across:F0} kind {s.Kind}")); }
                    }
                }
                for (int t = 0; t < 4; t++)
                {
                    (int col, int row, int dx, int dy) = lc.Shore[(int)(RiverHash.Unit(Seed, lc.Lake, t, 34) * lc.Shore.Count) % lc.Shore.Count];
                    // The direction points from the ring cell to its flooded neighbour: from a cell and a half outside the ring, through the ring cell centre, to the middle of the flooded cell.
                    double cx = (col + 0.5) * aroundStep, cy = (row + 0.5) * acrossStep, ux = dx * aroundStep, uy = dy * acrossStep, length = Math.Sqrt(ux * ux + uy * uy);
                    ux /= length; uy /= length;
                    double from = -1.5 * length, to = length;
                    Sample previous = default;
                    bool have = false;
                    for (double x = from; x <= to; x += 2d)
                    {
                        Sample cur = Eval(cx + ux * x, cy + uy * x);
                        if (cur.Wet && cur.Weight < 1d - 1e-9) st.WetNoWeight++;
                        if (have && cur.Wet != previous.Wet)
                        {
                            Sample wet = cur.Wet ? cur : previous, dry = cur.Wet ? previous : cur;
                            double violation = wet.Level - dry.Height;
                            if (dry.Kind == InlandWaterKind.River)
                            {
                                double fall = Math.Abs(wet.Level - dry.Level);
                                st.WaterlinePairs++; if (fall > 0.6) st.WaterlineBad++;
                                if (fall > st.WaterlineMax) st.WaterlineMax = fall;
                            }
                            else
                            {
                                st.LeakPairs++;
                                if (violation > 0.05) st.LeakBad++;
                                if (violation > 0.5) st.Example(FormattableString.Invariant($"lake containment {violation:F2} m: lake {lc.Lake} (level {lake.Level:F0}) cell {col},{row} x {x:F0} wet height {wet.Height:F2} level {wet.Level:F2} kind {wet.Kind}; dry height {dry.Height:F2} kind {dry.Kind}"));
                                if (violation > st.LeakMax) st.LeakMax = violation;
                            }
                        }
                        previous = cur; have = true; st.Pairs++;
                    }
                }
            }
            lock (locker) total.Merge(st);
        });
        return total;
    }

    private static string LakeText(string label, LakeStats s)
    {
        float[] sorted = s.PlugHeights.OrderBy(h => h).ToArray();
        double p50 = sorted.Length > 0 ? sorted[sorted.Length / 2] : 0, p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
        return FormattableString.Invariant($"{label}: {s.Samples} hashed samples ({s.LakeKind} Lake kind, {s.WetSamples} wet, {s.WetNoWeight} wet with weight below 1), ring samples {s.RingSamples}: plug count {s.Plugs}, plug height p50 {p50:F2} m, p99 {p99:F2} m, max {s.PlugMax:F2} m, over 5 m on {s.RingOver5} ({(double)s.RingOver5 / Math.Max(1, s.RingSamples):P2}); transects {s.Pairs} samples: containment {s.LeakBad}/{s.LeakPairs} wet-dry pairs with ground below the level over 0.05 m (largest {s.LeakMax:F2} m), waterline {s.WaterlineBad}/{s.WaterlinePairs} over 0.6 m (largest {s.WaterlineMax:F2} m)");
    }

    /// <summary>River and lake levels at the inlets and outlets of the lakes of the strip: the displayed level of the stem at its end (inlet) or where it leaves the membership (outlet) against the lake's whole-metre level.</summary>
    private long _outsideOutlets, _uphillOutlets;

    private (long Joins, long Bad, double Max) JoinLevels(double tamper, RiverCarveOperator op)
    {
        long joins = 0, bad = 0, outsideOutlets = 0, uphillOutlets = 0;
        double max = 0;
        double lo = _stripAroundMin + 20_000, hi = _stripAroundMax - 20_000;
        for (int l = 0; l < _graph.Lakes.Length; l++)
        {
            RiverLake lake = _graph.Lakes[l];
            double around = lake.MinCol * _graph.AroundStepMeters;
            if (lake.CellCount == 0 || around < lo || around > hi) continue;
            void Compare(double level, string what, int stem, double s)
            {
                joins++;
                double d = Math.Abs(level + tamper - lake.Level);
                if (d > 0.1) { bad++; if (bad <= 8 && tamper == 0d) Detail(FormattableString.Invariant($"join {what}: lake {l} level {lake.Level:F0}, stem {stem} (terminal {_net.Stems[stem].Terminal} {_net.Stems[stem].TerminalId}, receiving level {_net.ReceivingLevel[stem]:F2}, length {_net.Curves[stem].Length:F0}) s {s:F1} reads {level + tamper:F3}")); }
                if (d > max) max = d;
            }
            for (int i = 0; i < lake.InletCount; i++)
            {
                RiverInlet inlet = _graph.Inlets[lake.InletStart + i];
                Compare(RiverLevel.AtStem(_net, inlet.Stem, inlet.S), "inlet", inlet.Stem, inlet.S);
            }
            if (lake.OutletStem >= 0 && _net.Stems[lake.OutletStem].Terminal == RiverTerminal.Lake && _graph.Lakes[_net.Stems[lake.OutletStem].TerminalId].Level > lake.Level) uphillOutlets++;
            else if (lake.OutletStem >= 0)
            {
                // The outlet reads the lake level until it has crossed the ring (S8): compared at its start when that lies inside the membership. An outlet that starts on the ring or outside it spills over a sill and holds nothing.
                if (lake.OutletS > 0d)
                {
                    Compare(RiverLevel.AtStem(_net, lake.OutletStem, 0d), "outlet start", lake.OutletStem, 0d);
                }
                else outsideOutlets++;
            }
        }
        _outsideOutlets = outsideOutlets; _uphillOutlets = uphillOutlets;
        return (joins, bad, max);
    }

    private void Lakes()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var op = new RiverCarveOperator(_graph);
        LakeStats real = RunLakes(null, null, op);
        Detail(LakeText("lakes, real operator", real) + FormattableString.Invariant($"; {clock.Elapsed.TotalSeconds:F1} s"));
        foreach (string e in real.Examples) Detail("example " + e);
        Check(real.Samples >= 5000 && real.LakeKind > 500 && real.RingSamples > 1000, $"too little lake ground sampled ({real.Samples} samples, {real.LakeKind} Lake kind, {real.RingSamples} ring)");
        Check(real.WetNoWeight == 0, $"{real.WetNoWeight} wet lake samples where the corridor weight is below 1");
        Check(real.LeakPairs > 200, $"too few lake wet-dry pairs ({real.LeakPairs})");
        Check(real.LeakBad <= real.LeakPairs * 0.005 && real.LeakMax <= 0.5, $"lake containment: {real.LeakBad}/{real.LeakPairs} wet-dry pairs below the level, largest violation {real.LeakMax:F2} m");
        Check(real.WaterlineBad <= real.WaterlinePairs * 0.005 && real.WaterlineMax <= 1d, $"lake waterline levels: {real.WaterlineBad}/{real.WaterlinePairs}, largest {real.WaterlineMax:F2} m");
        Check(real.RingOver5 <= real.RingSamples * 0.01, $"plugs: {real.RingOver5}/{real.RingSamples} ring samples over 5 m (limit 1 %)");
        if (real.PlugMax > 25d) Detail(FormattableString.Invariant($"ESCALATION (T2R-8): a plug of {real.PlugMax:F1} m stands on the ring (limit 25 m); see the plug examples (a lake beside a barrier cell stands above the barrier ground, S8 Barrier cap)"));
        (long joins, long bad, double max) = JoinLevels(0d, op);
        Detail(FormattableString.Invariant($"joins ({_outsideOutlets} outlets start outside the membership and {_uphillOutlets} drain into a higher lake (a cascade the lake balance left uphill); neither is compared): {joins} inlet and outlet levels compared with the lake level, {bad} differ by more than 0.1 m (largest {max:F3} m)"));
        Check(joins > 20 && bad == 0, $"river and lake levels differ by more than 0.1 m at {bad} of {joins} inlets and outlets (largest {max:F2} m)");

        // Negative controls, each against the final code. 1: the plug off on a fabricated gap (the ground dips 3 m under the level across the ring band) must leak; with the plug on it must not.
        var noPlug = new RiverCarveOperator(_graph) { Plug = false };
        Func<double, double, double, double> Dip(RiverCarveOperator checker) => (around, across, g) =>
        {
            double p = checker.LakeMembership(around, across, out double level, out _);
            return !double.IsNaN(level) && Math.Abs(p) < 0.45 ? Math.Min(g, level - 3d) : g;
        };
        LakeStats gapOn = RunLakes(Dip(op), null, op);
        LakeStats gapOff = RunLakes(Dip(noPlug), null, noPlug);
        Detail(LakeText("fabricated gap, plug on", gapOn));
        Detail(LakeText("fabricated gap, plug OFF", gapOff));
        Check(gapOff.LeakBad > gapOff.LeakPairs * 0.005 && gapOff.LeakMax > 0.5, $"negative control: the lake containment check did not fail with the plug off on a fabricated gap ({gapOff.LeakBad}/{gapOff.LeakPairs}, largest {gapOff.LeakMax:F2} m)");
        Check(gapOn.LeakBad <= gapOn.LeakPairs * 0.005 && gapOn.LeakMax <= 0.5, $"the fabricated gap leaked with the plug on ({gapOn.LeakBad}/{gapOn.LeakPairs}, largest {gapOn.LeakMax:F2} m)");
        // 2: a weight that reads 0 on the water must be flagged. 3: a plug 10 m too high on every ring sample must be flagged. 4: levels 0.5 m off at the joins must be flagged.
        LakeStats zero = RunLakes(null, s => s with { Weight = 0d }, op);
        Check(zero.WetNoWeight > 0, "negative control: a zero corridor weight on the water was not flagged");
        LakeStats tall = RunLakes(null, s => s with { Delta = s.Delta + 10d }, op);
        Check(tall.RingOver5 > tall.RingSamples * 0.01, $"negative control: plugs 10 m too high were not flagged ({tall.RingOver5}/{tall.RingSamples})");
        (long _, long badJoins, double _) = JoinLevels(0.5d, op);
        Check(badJoins > 0, "negative control: join levels 0.5 m off were not flagged");
        Detail($"controls: plug off leaks {gapOff.LeakBad}/{gapOff.LeakPairs} (largest {gapOff.LeakMax:F2} m), zero weight flags {zero.WetNoWeight}, tall plug flags {tall.RingOver5}/{tall.RingSamples}, shifted joins flag {badJoins}/{joins}");
    }

    // ------------------------------------------------------------------ rivers reach the lake's water (T2R-8 review)

    private sealed class ReachStats
    {
        public int Ends, DryEnds, Rastered, Apart, Lakes;
        public double GapMax;
        public readonly List<string> Examples = new();
    }

    /// <summary>
    /// Every stem that ends in a lake and every outlet, on the strip: the natural ground at the stem's lake end must stand under the lake level (DryEnds counts the others). On up
    /// to 24 hashed lakes of at most 12 cells a side, the water is also rastered at 16 m (wet samples of the carved model, plus the wet centreline samples of the lake's stems,
    /// which keeps a channel narrower than a pixel in one piece): the lake end of each of its stems must lie in the same body of water as most of the lake (8-connected), and
    /// Apart counts those that do not, with the distance from the end to that body (the dry gap the M2 lake picture showed: 1.8 km at an inlet, 0.8 km at the outlet).
    /// </summary>
    private ReachStats LakeReach(RiverGraph graph)
    {
        RiverNetwork net = graph.Network;
        var total = new ReachStats();
        double lo = _stripAroundMin + 20_000, hi = _stripAroundMax - 20_000;
        var ends = new List<(int Lake, int Stem, double S)>();
        for (int l = 0; l < graph.Lakes.Length; l++)
        {
            RiverLake lake = graph.Lakes[l];
            double around = lake.MinCol * graph.AroundStepMeters;
            if (lake.CellCount == 0 || around < lo || around > hi) continue;
            for (int i = 0; i < lake.InletCount; i++) ends.Add((l, graph.Inlets[lake.InletStart + i].Stem, net.Curves[graph.Inlets[lake.InletStart + i].Stem].Length));
            if (lake.OutletStem >= 0) ends.Add((l, lake.OutletStem, 0d));
        }
        using (TerrainGenerationHeightModel natural = new(_recipe, false))
            foreach ((int l, int stem, double s) in ends)
            {
                net.Centre(stem, s, out double a, out double c, out _, out _);
                double g = natural.CalculateHeight(a, c);
                total.Ends++;
                if (g >= graph.Lakes[l].Level) { total.DryEnds++; if (total.Examples.Count < 6) total.Examples.Add(FormattableString.Invariant($"dry end: lake {l} (level {graph.Lakes[l].Level:F0}) stem {stem} s {s:F0} ground {g:F1}")); }
            }
        const double pixel = 16d;
        List<int> picks = ends.Select(e => e.Lake).Distinct().Where(l => graph.Lakes[l].MaxCol - graph.Lakes[l].MinCol < 12 && graph.Lakes[l].MaxRow - graph.Lakes[l].MinRow < 12 && graph.Lakes[l].MinRow > 4 && graph.Lakes[l].MaxRow < graph.Rows - 5)
            .OrderBy(l => RiverHash.Mix(Seed, l, 95)).Take(24).ToList();
        var locker = new object();
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel model = new(_recipe, true, graph);
            for (int pi = w; pi < picks.Count; pi += _workers)
            {
                int l = picks[pi];
                RiverLake lake = graph.Lakes[l];
                double around0 = (lake.MinCol - 1.5) * graph.AroundStepMeters, across0 = (lake.MinRow - 1.5) * graph.AcrossStepMeters;
                int width = (int)((lake.MaxCol - lake.MinCol + 4) * graph.AroundStepMeters / pixel), height = (int)((lake.MaxRow - lake.MinRow + 4) * graph.AcrossStepMeters / pixel);
                var wet = new byte[width * height]; // 1 river water, 2 lake water
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(around0 + (x + 0.5) * pixel, across0 + (y + 0.5) * pixel);
                        if (d.Height < d.WaterLevel) wet[y * width + x] = d.InlandWater == InlandWaterKind.Lake ? (byte)2 : d.InlandWater == InlandWaterKind.River ? (byte)1 : (byte)0;
                    }
                List<(int Lake, int Stem, double S)> mine = ends.Where(e => e.Lake == l).ToList();
                foreach ((int _, int stem, double _) in mine)
                {
                    double length = net.Curves[stem].Length;
                    for (double s = 0; s <= length; s += 8d)
                    {
                        net.Centre(stem, s, out double a, out double c, out _, out _);
                        int x = (int)Math.Floor((a - around0) / pixel), y = (int)Math.Floor((c - across0) / pixel);
                        if ((uint)x >= (uint)width || (uint)y >= (uint)height || wet[y * width + x] != 0) continue;
                        TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(a, c);
                        if (d.InlandWater != InlandWaterKind.None && d.Height < d.WaterLevel) wet[y * width + x] = 1;
                    }
                }
                // 8-connected bodies of water; the lake's body is the one with the most lake water.
                var label = new int[width * height];
                var lakePixels = new List<int> { 0 };
                var stack = new Stack<int>();
                for (int start = 0; start < wet.Length; start++)
                {
                    if (wet[start] == 0 || label[start] != 0) continue;
                    int id = lakePixels.Count, count = 0;
                    label[start] = id; stack.Push(start);
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(), x = i % width, y = i / width;
                        if (wet[i] == 2) count++;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                                int n = ny * width + nx;
                                if (wet[n] != 0 && label[n] == 0) { label[n] = id; stack.Push(n); }
                            }
                    }
                    lakePixels.Add(count);
                }
                int body = 0;
                for (int id = 1; id < lakePixels.Count; id++) if (body == 0 || lakePixels[id] > lakePixels[body]) body = id;
                var st = new ReachStats { Lakes = 1 };
                foreach ((int _, int stem, double s) in mine)
                {
                    net.Centre(stem, s, out double a, out double c, out _, out _);
                    double ex = (a - around0) / pixel, ey = (c - across0) / pixel;
                    st.Rastered++;
                    double gap2 = double.PositiveInfinity;
                    for (int i = 0; i < label.Length && body > 0; i++)
                    {
                        if (label[i] != body) continue;
                        double dx = i % width + 0.5 - ex, dy = i / width + 0.5 - ey, d2 = dx * dx + dy * dy;
                        if (d2 < gap2) gap2 = d2;
                    }
                    double gap = double.IsInfinity(gap2) ? 99_999d : Math.Sqrt(gap2) * pixel;
                    if (gap <= 2d * pixel) continue;
                    st.Apart++;
                    st.GapMax = Math.Max(st.GapMax, gap);
                    st.Examples.Add(FormattableString.Invariant($"apart: lake {l} (level {lake.Level:F0}, {lake.CellCount} cells) stem {stem} s {s:F0} stands {gap:F0} m from the lake's water"));
                }
                lock (locker)
                {
                    total.Lakes += st.Lakes; total.Rastered += st.Rastered; total.Apart += st.Apart; total.GapMax = Math.Max(total.GapMax, st.GapMax);
                    foreach (string e in st.Examples) if (total.Examples.Count < 14) total.Examples.Add(e);
                }
            }
        });
        return total;
    }

    private static string ReachText(string label, ReachStats s)
        => FormattableString.Invariant($"{label}: {s.Ends} inlet and outlet ends on the strip, {s.DryEnds} on natural ground at or above the lake level; {s.Lakes} lakes rastered at 16 m with {s.Rastered} ends, {s.Apart} apart from the lake's water (largest gap {s.GapMax:F0} m)");

    /// <summary>
    /// T2R-8 review: reported, not yet checked. As built in T2R-8 the stems end at the first flooded cell and start at the first dry one, and most of them stand apart from the
    /// lake's water. T2R-8b (docs/work-orders/terrain-vegetation/T2.md) runs the stems through the lake and turns the two counts into checks, with a control.
    /// </summary>
    private void LakeWater()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ReachStats real = LakeReach(_graph);
        Detail(ReachText("rivers reach the lake (reported only until T2R-8b)", real) + FormattableString.Invariant($"; {clock.Elapsed.TotalSeconds:F1} s"));
        foreach (string e in real.Examples.Take(8)) Detail("example " + e);
        Check(real.Ends >= 100 && real.Rastered >= 20, $"too few lake ends looked at ({real.Ends} on the strip, {real.Rastered} rastered)");
    }

    private (long Samples, long Over40, double CutMax, long Pairs, long Rising, long Steps, long StepsOver) _path;

    /// <summary>Cut along the path, the level never rising downstream, and the carved height along the centreline.</summary>
    private void Paths()
    {
        long samples = 0, over40 = 0, pairs = 0, rising = 0, drawn = 0;
        double cutMax = 0;
        var locker = new object();
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel model = NewModel();
            long s0 = 0, o0 = 0, p0 = 0, r0 = 0, dr0 = 0;
            double m0 = 0;
            for (int i = w; i < _reaches.Count; i += _workers)
            {
                RiverReach reach = _net.Reaches[_reaches[i]];
                double previous = double.NaN;
                for (double s = reach.SStart; s <= reach.SEnd; s += 5d)
                {
                    _net.Centre(reach.Stem, s, out double around, out double across, out _, out _);
                    TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(around, across);
                    if (d.Height - d.RiverGroundDelta < 2d) { previous = double.NaN; continue; }
                    s0++;
                    double cut = -d.RiverGroundDelta;
                    if (cut > 40d) o0++;
                    if (cut > m0) m0 = cut;
                    bool own = d.RiverReach >= 0 && _net.Reaches[d.RiverReach].Stem == reach.Stem;
                    if (own)
                    {
                        if (!double.IsNaN(previous)) { p0++; if (d.WaterLevel > previous + 1e-6) r0++; }
                        previous = d.WaterLevel;
                        if (d.WaterLevel < RiverLevel.AtStem(_net, reach.Stem, s) - 1e-6) dr0++;
                    }
                    else previous = double.NaN;
                }
            }
            lock (locker) { samples += s0; over40 += o0; pairs += p0; rising += r0; drawn += dr0; cutMax = Math.Max(cutMax, m0); }
        });
        Detail($"path: cut over 40 m on {over40}/{samples} centreline samples every 5 m ({(double)over40 / Math.Max(1, samples):P2}), largest cut {cutMax:F1} m; level rises downstream on {rising}/{pairs} owner pairs; drawn-down level (below the stem's own) on {drawn}/{samples} samples"
            + $" ({(double)drawn / Math.Max(1, samples):P2})");
        Check(samples > 10000, "too few path samples");
        // Tuned in the T2R-7 review: 5.5 % is the state the T2R-5 review accepted until the M2 visual review of carved ground (was 3 %).
        Check(over40 <= samples * 0.055, $"cut over 40 m on {(double)over40 / Math.Max(1, samples):P2} of path length (limit 5.5 %)");
        Check(rising <= pairs * 0.005, $"the level rises downstream on {rising}/{pairs} owner pairs");
    }

    /// <summary>
    /// T2R-7b: the analytic slope of <see cref="RiverNetwork.MeanderWithSlope"/> against a central difference of <see cref="RiverNetwork.Meander(int, double)"/> at hashed points
    /// (away from the reach joints, where a reach shorter than the 200 m blend leaves a kink), the two values of D equal, and the stems' true reach inside the index radius.
    /// </summary>
    private void MeanderSlope()
    {
        const int points = 2000;
        double worst = 0, worstD = 0, worstReach = 0;
        int tested = 0, worstStem = -1;
        double worstS = 0;
        for (int i = 0; i < points; i++)
        {
            int stem = (int)(RiverHash.Unit(Seed, i, 21, 5) * _net.Stems.Length) % _net.Stems.Length;
            double length = _net.Curves[stem].Length;
            double s = 2d + RiverHash.Unit(Seed, i, 22, 5) * (length - 4d);
            bool nearJoint = false;
            for (int r = _net.Stems[stem].ReachStart, end = r + _net.Stems[stem].ReachCount; r < end; r++)
                if (Math.Abs(_net.Reaches[r].SStart - s) < 2d || Math.Abs(_net.Reaches[r].SEnd - s) < 2d) nearJoint = true;
            if (nearJoint) continue;
            _net.ChannelAt(stem, s, out RiverChannel c);
            double d = _net.MeanderWithSlope(stem, s, in c, out double slope);
            const double h = 0.5;
            double central = (_net.Meander(stem, s + h) - _net.Meander(stem, s - h)) / (2d * h);
            tested++;
            worstD = Math.Max(worstD, Math.Abs(d - _net.Meander(stem, s, in c)));
            if (Math.Abs(slope - central) > worst) { worst = Math.Abs(slope - central); worstStem = stem; worstS = s; }
        }
        for (int stem = 0; stem < _net.Stems.Length; stem++) worstReach = Math.Max(worstReach, _net.StemLateralReach(stem, RiverCarveOperator.FadeEndMeters));
        Detail($"meander slope: {tested} hashed points, largest difference between the analytic slope and a central difference {worst:F5} (stem {worstStem}, s {worstS:F0}), largest difference of D {worstD:E1}; largest stem reach {worstReach:F0} m against the index radius {RiverSpatialIndex.ReachRadiusMeters:F0} m");
        Check(tested > points / 2, "meander slope check: too few points tested");
        Check(worst < 0.01, $"meander slope: analytic and central difference differ by {worst:F5} (stem {worstStem}, s {worstS:F0})");
        Check(worstD < 1e-9, $"meander slope: D differs from Meander by {worstD:E1}");
        Check(worstReach <= RiverSpatialIndex.ReachRadiusMeters, $"a stem's lateral reach {worstReach:F0} m exceeds the index radius {RiverSpatialIndex.ReachRadiusMeters:F0} m");
    }

    // ------------------------------------------------------------------ footprint

    private void Footprints()
    {
        long pairs = 0, levelBad = 0, deeper = 0, missing = 0, crossings = 0, joinPairs = 0, joinDeeper = 0;
        double levelMax = 0;
        var locker = new object();
        // Level the same at footprints 0, 8 and 32, and a coarser footprint never deeper: the channel centre of every fourth section.
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel model = NewModel();
            long p0 = 0, l0 = 0, d0 = 0, m0 = 0, c0 = 0, jp = 0, jd = 0;
            double lm = 0;
            for (int i = w * 4; i < _sections.Count; i += 4 * _workers)
            {
                Section sec = _sections[i];
                Sample f0 = Real(model, sec.Around, sec.Across, 0d), f8 = Real(model, sec.Around, sec.Across, 8d), f32 = Real(model, sec.Around, sec.Across, 32d);
                if (f0.Kind != InlandWaterKind.River || f8.Kind != InlandWaterKind.River || f32.Kind != InlandWaterKind.River) continue;
                p0++;
                double dl = Math.Max(Math.Abs(f0.Level - f8.Level), Math.Abs(f0.Level - f32.Level));
                if (dl > 1e-9) l0++;
                lm = Math.Max(lm, dl);
                bool isDeeper = f8.Height < f0.Height - 1e-9 || f32.Height < f8.Height - 1e-9;
                if (sec.Confluence) { jp++; if (isDeeper) jd++; }
                else if (isDeeper) d0++;
            }
            // A lattice of 8 m or 32 m across 50 reaches has a wet vertex at every crossing.
            for (int i = w; i < Math.Min(50, _reaches.Count); i += _workers)
            {
                RiverReach reach = _net.Reaches[_reaches[i]];
                double mid = 0.5 * (reach.SStart + reach.SEnd);
                foreach (double fp in new[] { 8d, 32d })
                {
                    double prevA = double.NaN, prevC = 0;
                    for (double s = Math.Max(reach.SStart, mid - 200); s <= Math.Min(reach.SEnd, mid + 200); s += 1d)
                    {
                        _net.Centre(reach.Stem, s, out double a, out double c, out _, out _);
                        if (!double.IsNaN(prevA))
                        {
                            foreach (bool alongAround in new[] { true, false })
                            {
                                double p0v = alongAround ? prevA : prevC, p1v = alongAround ? a : c;
                                for (double line = Math.Ceiling(Math.Min(p0v, p1v) / fp) * fp; line <= Math.Max(p0v, p1v) && p0v != p1v; line += fp)
                                {
                                    double u = (line - p0v) / (p1v - p0v), ca = prevA + (a - prevA) * u, cc = prevC + (c - prevC) * u;
                                    if (u < 0 || u > 1) continue;
                                    c0++;
                                    bool wet = false;
                                    for (int k = -1; k <= 2 && !wet; k++)
                                    {
                                        double va = alongAround ? line : (Math.Floor(ca / fp) + k) * fp, vc = alongAround ? (Math.Floor(cc / fp) + k) * fp : line;
                                        wet = Real(model, va, vc, fp).Wet;
                                    }
                                    if (!wet) m0++;
                                }
                            }
                        }
                        prevA = a; prevC = c;
                    }
                }
            }
            lock (locker) { pairs += p0; levelBad += l0; deeper += d0; missing += m0; crossings += c0; joinPairs += jp; joinDeeper += jd; levelMax = Math.Max(levelMax, lm); }
        });
        Detail($"footprint: level equal at 0, 8 and 32 m on {pairs - levelBad}/{pairs} sections (largest difference {levelMax:F2} m), coarser deeper on {deeper}/{pairs - joinPairs} reach sections and {joinDeeper}/{joinPairs} confluence sections; lattice crossings without a wet vertex {missing}/{crossings}");
        Check(pairs > 200, "too few footprint samples");
        Check(levelBad <= pairs * 0.005, $"the level changes with the footprint on {levelBad}/{pairs} sections");
        // Tuned in the T2R-7 review: the 99.5 % bar holds on reach sections. At a confluence the widened core of the other channel covers this one's centre (two channels
        // closer than the lattice merge into the lower one), so those sections are printed and held to 5 %.
        Check(deeper <= (pairs - joinPairs) * 0.005, $"a coarser footprint is deeper on {deeper}/{pairs - joinPairs} reach sections");
        Check(joinDeeper <= joinPairs * 0.05, $"a coarser footprint is deeper on {joinDeeper}/{joinPairs} confluence sections (limit 5 %)");
        Check(crossings > 100 && missing <= crossings * 0.005, $"lattice crossings without a wet vertex: {missing}/{crossings}");
    }

    // ------------------------------------------------------------------ points far from rivers and sites

    private void FarPoints()
    {
        using TerrainGenerationHeightModel model = NewModel();
        using TerrainGenerationHeightModel natural = new(_recipe, true);
        int[] nodeAround = _net.NodeAroundDm, nodeAcross = _net.NodeAcrossDm;
        long tested = 0, bad = 0, nearMiss = 0;
        for (int i = 0; i < 4000; i++)
        {
            double around = _stripAroundMin + 20_000 + RiverHash.Unit(Seed, i, 1, 5) * (_stripAroundMax - _stripAroundMin - 40_000), across = 1000 + RiverHash.Unit(Seed, i, 2, 6) * (_net.WidthM - 2000);
            double nearest = double.PositiveInfinity;
            for (int n = 0; n < nodeAround.Length; n++)
            {
                double da = Math.Abs(nodeAround[n] / 10d - around), dc = nodeAcross[n] / 10d - across;
                if (da > 3000 || Math.Abs(dc) > 3000) continue;
                nearest = Math.Min(nearest, Math.Sqrt(da * da + dc * dc));
            }
            if (nearest < 2000d || _graph.IsNear(around, across)) continue;
            tested++;
            TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(around, across);
            if (d.RiverCorridorWeight != 0d || d.InlandWater != InlandWaterKind.None || d.RiverReach != -1 || !double.IsNaN(d.WaterLevel)) bad++;
            if (d.Height != natural.CalculateHeight(around, across)) nearMiss++;
        }
        Detail($"points over 2 km from every node: {tested} tested, {bad} with a weight, owner or level, {nearMiss} with a height different from the natural model");
        Check(tested > 500 && bad == 0 && nearMiss == 0, $"the operator acted on {bad + nearMiss} of {tested} points far from every river");
    }

    private void Sites()
    {
        TerrainGenerationParameters without = EmitterSiteTestWorlds.Gen17WorldRecipe((int)Seed, 1);
        ulong a = EmitterSiteTerrainShaper.GetOrCreate(without).Registry.Identity, b = EmitterSiteTerrainShaper.GetOrCreate(_recipe).Registry.Identity;
        Detail($"site registry identity without rivers {a:X16}, with rivers {b:X16}");
        Check(a == b, "the site registry identity differs with and without rivers");
        // The model without a graph behaves as the natural-plus-sites model.
        using var noGraph = new TerrainGenerationHeightModel(_recipe, true);
        using var plain = new TerrainGenerationHeightModel(without, true);
        int differ = 0;
        for (int i = 0; i < 200; i++)
        {
            double around = _stripAroundMin + RiverHash.Unit(Seed, i, 3, 7) * (_stripAroundMax - _stripAroundMin), across = RiverHash.Unit(Seed, i, 4, 8) * _net.WidthM;
            if (noGraph.CalculateHeight(around, across) != plain.CalculateHeight(around, across)) differ++;
        }
        Check(differ == 0, $"a model without a graph differs from the generation 1 model on {differ} of 200 points");
    }

    // ------------------------------------------------------------------ cost

    private void Cost()
    {
        using TerrainGenerationHeightModel natural = new(_recipe, false);
        var op = new RiverCarveOperator(_graph);
        var nearPoints = new List<(double, double, double)>();
        var shoulderPoints = new List<(double, double, double)>();
        var elsewhere = new List<(double, double, double)>();
        for (int i = 0; i < _sections.Count && nearPoints.Count < 20000; i++)
        {
            Section sec = _sections[i];
            for (int k = 0; k < 8; k++)
            {
                double x = (RiverHash.Unit(Seed, i, k, 9) * 2 - 1) * 150d, around = sec.Around + x * sec.Nx, across = sec.Across + x * sec.Ny;
                nearPoints.Add((around, across, natural.CalculateHeight(around, across)));
            }
        }
        for (int i = 0; i < 20000 && shoulderPoints.Count < 10000; i++)
        {
            Section sec = _sections[i % _sections.Count];
            double x = (RiverHash.Unit(Seed, i, 11, 12) > 0.5 ? 1 : -1) * (320d + RiverHash.Unit(Seed, i, 13, 14) * 300d), around = sec.Around + x * sec.Nx, across = sec.Across + x * sec.Ny;
            if (!_graph.IsNear(around, across)) continue;
            shoulderPoints.Add((around, across, natural.CalculateHeight(around, across)));
        }
        for (int i = 0; elsewhere.Count < 10000 && i < 200000; i++)
        {
            double around = _stripAroundMin + RiverHash.Unit(Seed, i, 15, 16) * (_stripAroundMax - _stripAroundMin), across = RiverHash.Unit(Seed, i, 17, 18) * _net.WidthM;
            if (_graph.IsNear(around, across)) continue;
            elsewhere.Add((around, across, 10d));
        }
        double Time(List<(double Around, double Across, double G)> pts)
        {
            double best = double.MaxValue;
            double sink = 0;
            for (int pass = 0; pass < 5; pass++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach ((double a, double c, double g) in pts) sink += op.Evaluate(a, c, g, 0d).Height;
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds * 1000d / pts.Count);
            }
            if (double.IsNaN(sink)) Fail("cost loop produced NaN");
            return best;
        }
        double near = Time(nearPoints), shoulder = Time(shoulderPoints), far = Time(elsewhere);
        // Reference: what one natural height sample costs on the same points (the operator's cost is paid on top of it).
        double naturalCost = double.MaxValue, naturalSink = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < nearPoints.Count; i += 4) naturalSink += natural.CalculateHeight(nearPoints[i].Item1, nearPoints[i].Item2);
            naturalCost = Math.Min(naturalCost, clock.Elapsed.TotalMilliseconds * 1000d / ((nearPoints.Count + 3) / 4));
        }
        if (double.IsNaN(naturalSink)) Fail("natural cost loop produced NaN");
        Detail(FormattableString.Invariant($"natural height sample on the same points: {naturalCost:F3} us; the operator near a river adds {near / naturalCost:P0}"));
        Detail(FormattableString.Invariant($"operator cost (one thread, best of 5, indicative): within 150 m of a centreline {near:F3} us per sample ({nearPoints.Count} points), Near but beyond 300 m {shoulder:F3} us ({shoulderPoints.Count}), elsewhere {far:F3} us ({elsewhere.Count}); gate +{NearCostGateMicroseconds:F1} us near rivers, +0.03 us elsewhere"));
        Check(near <= NearCostGateMicroseconds, FormattableString.Invariant($"the operator costs {near:F3} us per sample near rivers (limit {NearCostGateMicroseconds:F1})"));
    }

    // ------------------------------------------------------------------ pictures (optional)

    private void Pictures()
    {
        string? folder = System.Environment.GetEnvironmentVariable("RIVER_CARVE_PICTURES");
        if (string.IsNullOrEmpty(folder)) return;
        System.IO.Directory.CreateDirectory(folder);
        using TerrainGenerationHeightModel natural = new(_recipe, false);
        bool Usable(int stem, double s)
        {
            _net.Centre(stem, s, out double around, out double across, out _, out _);
            return around > _stripAroundMin + 20_000 && around < _stripAroundMax - 20_000 && across > 5000 && across < _net.WidthM - 5000 && natural.CalculateHeight(around, across) >= 20d;
        }
        // The reach: the widest long reach on land above 20 m. The confluence: the widest tributary with a usable junction.
        int bestReach = -1; double bestWidth = 0;
        for (int r = 0; r < _net.Reaches.Length; r++)
        {
            RiverReach reach = _net.Reaches[r];
            _net.ChannelAt(reach.Stem, 0.5 * (reach.SStart + reach.SEnd), out RiverChannel channel);
            double w = 2d * channel.HalfWidth;
            if (reach.SEnd - reach.SStart < 3000 || w <= bestWidth || w > 80 || !Usable(reach.Stem, 0.5 * (reach.SStart + reach.SEnd))) continue;
            bestReach = r; bestWidth = w;
        }
        int bestStem = -1; double bestTrib = 0;
        for (int i = 0; i < _net.Stems.Length; i++)
        {
            RiverStem st = _net.Stems[i];
            if (st.Terminal != RiverTerminal.Confluence || !Usable(st.TerminalId, st.JunctionS)) continue;
            RiverReach last = _net.Reaches[st.ReachStart + st.ReachCount - 1];
            _net.ChannelAt(i, 0.5 * (last.SStart + last.SEnd), out RiverChannel channel);
            double w = 2d * channel.HalfWidth;
            if (w > bestTrib && w <= 60) { bestTrib = w; bestStem = i; }
        }
        Check(bestReach >= 0 && bestStem >= 0, "no reach or confluence found for the pictures");
        if (bestReach < 0 || bestStem < 0) return;
        RiverReach rr = _net.Reaches[bestReach];
        _net.Centre(rr.Stem, 0.5 * (rr.SStart + rr.SEnd), out double ra, out double rc, out _, out _);
        _net.Centre(_net.Stems[bestStem].TerminalId, _net.Stems[bestStem].JunctionS, out double ja, out double jc, out _, out _);
        Detail($"pictures: reach {bestReach} (stem {rr.Stem}, width {bestWidth:F0} m) at around {ra:F0} across {rc:F0}; confluence of stem {bestStem} (width {bestTrib:F0} m) into stem {_net.Stems[bestStem].TerminalId} at around {ja:F0} across {jc:F0}");
        Render(System.IO.Path.Combine(folder, "reach"), ra, rc, 5d);
        Render(System.IO.Path.Combine(folder, "confluence"), ja, jc, 5d);
        Render(System.IO.Path.Combine(folder, "reach_zoom"), ra, rc, 1d);
        Render(System.IO.Path.Combine(folder, "confluence_zoom"), ja, jc, 1d);
        DumpSections(folder);
        DumpProfile(folder);
        DumpLake(folder);
    }


    // ------------------------------------------------------------------ M2 data for the pictures (T2R-8): cross-sections, a pools-and-chutes profile, a lake shoreline

    private static void WriteFloats(string path, float[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        System.IO.File.WriteAllBytes(path, bytes);
    }

    /// <summary>One vale (the widest low-cut section) and one deep cut (the deepest reach section), every metre over the section's extent, natural against carved, with the water level.</summary>
    private void DumpSections(string folder)
    {
        using TerrainGenerationHeightModel natural = new(_recipe, false);
        using TerrainGenerationHeightModel carved = NewModel();
        Section? vale = null, deep = null;
        double valeScore = double.MinValue, deepCut = 0;
        foreach (Section sec in _sections.Where(x => !x.Confluence && !x.LakeTerminal && !x.LakeEnd))
        {
            if (natural.CalculateHeight(sec.Around, sec.Across) < 20d) continue;
            double maxCut = 0;
            for (double x = -sec.Extent; x <= sec.Extent; x += 5d)
                maxCut = Math.Max(maxCut, natural.CalculateHeight(sec.Around + x * sec.Nx, sec.Across + x * sec.Ny) - carved.CalculateHeight(sec.Around + x * sec.Nx, sec.Across + x * sec.Ny));
            _net.ChannelAt(sec.Stem, sec.S, out RiverChannel c);
            double score = (c.HalfWidth + c.BankWidth + c.Floodplain) - 10d * maxCut;
            if (c.HalfWidth >= 10d && maxCut < 12d && score > valeScore) { valeScore = score; vale = sec; }
            if (maxCut > deepCut && maxCut < 120d) { deepCut = maxCut; deep = sec; }
        }
        foreach ((string name, Section? sec) in new[] { ("vale", vale), ("deep_cut", deep) })
        {
            if (sec == null) { Fail("no section for the " + name + " picture"); continue; }
            var lines = new List<string> { "x_m,natural_m,carved_m,water_level_m" };
            for (double x = -sec.Extent; x <= sec.Extent; x += 1d)
            {
                double a = sec.Around + x * sec.Nx, c = sec.Across + x * sec.Ny;
                TerrainGenerationHeightModel.Diagnostics d = carved.SampleDiagnostics(a, c);
                bool wet = (d.InlandWater == InlandWaterKind.River || d.InlandWater == InlandWaterKind.Lake) && d.Height < d.WaterLevel;
                lines.Add(FormattableString.Invariant($"{x:F1},{natural.CalculateHeight(a, c):F2},{d.Height:F2},{(wet ? d.WaterLevel : double.NaN):F2}"));
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "section_" + name + ".csv"), lines);
            _net.ChannelAt(sec.Stem, sec.S, out RiverChannel ch);
            Detail($"section {name}: stem {sec.Stem} s {sec.S:F0}, half width {ch.HalfWidth:F1} m, bank {ch.BankWidth:F1} m, floodplain {ch.Floodplain:F0} m, deepest cut in the section {(name == "vale" ? 0 : deepCut):F1} m");
        }
    }

    /// <summary>A reach whose stored profile falls 3.5 to 5.5 %: stored profile, displayed level and the centreline ground every 2 m over 500 m.</summary>
    private void DumpProfile(string folder)
    {
        using TerrainGenerationHeightModel natural = new(_recipe, false);
        for (int r = 0; r < _net.Reaches.Length; r++)
        {
            RiverReach reach = _net.Reaches[r];
            if (reach.SEnd - reach.SStart < 700f || _net.Stems[reach.Stem].Terminal == RiverTerminal.Lake || _net.Reaches[_net.Stems[reach.Stem].ReachStart].UpstreamLake >= 0) continue;
            double s0 = 0.5 * (reach.SStart + reach.SEnd) - 250d, s1 = s0 + 500d;
            double slope = (RiverLevel.Stored(_net, reach.Stem, s0) - RiverLevel.Stored(_net, reach.Stem, s1)) / 500d;
            _net.Centre(reach.Stem, s0 + 250d, out double ca, out double cc, out _, out _);
            if (slope < 0.035 || slope > 0.055 || natural.CalculateHeight(ca, cc) < 20d) continue;
            var lines = new List<string> { "s_m,stored_profile_m,displayed_level_m,bed_m,centreline_ground_m" };
            for (double s = s0; s <= s1; s += 2d)
            {
                _net.Centre(reach.Stem, s, out double a, out double c, out _, out _);
                _net.ChannelAt(reach.Stem, s, out RiverChannel ch);
                double level = RiverLevel.AtStem(_net, reach.Stem, s);
                lines.Add(FormattableString.Invariant($"{s - s0:F1},{RiverLevel.Stored(_net, reach.Stem, s):F3},{level:F3},{level - ch.Depth:F3},{natural.CalculateHeight(a, c):F2}"));
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "profile.csv"), lines);
            Detail($"profile: reach {r} (stem {reach.Stem}), 500 m from s {s0:F0}, stored gradient {slope:P1}");
            return;
        }
        Fail("no 3 to 5.5 % reach found for the profile picture");
    }

    /// <summary>A compact lake of 25 to 120 cells with an inlet and an outlet: carved height, natural height, ground delta and the wet mask over an 8 km window at 8 m per pixel.</summary>
    private void DumpLake(string folder)
    {
        int pick = -1;
        foreach (LakeCase lc in LakeCases().OrderBy(c => RiverHash.Mix(Seed, c.Lake, 91)))
        {
            RiverLake l = _graph.Lakes[lc.Lake];
            int boxCells = (l.MaxCol - l.MinCol + 1) * (l.MaxRow - l.MinRow + 1);
            if (l.CellCount >= 25 && l.CellCount <= 120 && boxCells <= 2 * l.CellCount && l.InletCount >= 1 && l.OutletStem >= 0 && l.OutletS > 0f) { pick = lc.Lake; break; }
        }
        if (pick < 0) { Fail("no lake with an inlet and an outlet for the picture"); return; }
        RiverLake lake = _graph.Lakes[pick];
        const int size = 1000;
        double pixel = Math.Clamp((Math.Max(lake.MaxCol - lake.MinCol + 1, lake.MaxRow - lake.MinRow + 1) * 1000d + 3000d) / size, 3d, 12d);
        double centreAround = 0.5 * (lake.MinCol + lake.MaxCol + 1) * _graph.AroundStepMeters, centreAcross = 0.5 * (lake.MinRow + lake.MaxRow + 1) * _graph.AcrossStepMeters;
        double around0 = centreAround - 0.5 * size * pixel, across0 = centreAcross - 0.5 * size * pixel;
        var natural = new float[size * size]; var heights = new float[size * size]; var delta = new float[size * size]; var wet = new float[size * size];
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel model = NewModel();
            using TerrainGenerationHeightModel plain = new(_recipe, true);
            for (int row = w; row < size; row += _workers)
                for (int col = 0; col < size; col++)
                {
                    double a = around0 + (col + 0.5) * pixel, c = across0 + (row + 0.5) * pixel;
                    TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(a, c);
                    int i = row * size + col;
                    natural[i] = (float)plain.CalculateHeight(a, c); heights[i] = (float)d.Height; delta[i] = (float)(d.Height - natural[i]);
                    wet[i] = (d.InlandWater == InlandWaterKind.River || d.InlandWater == InlandWaterKind.Lake) && d.Height < d.WaterLevel ? (d.InlandWater == InlandWaterKind.Lake ? 2f : 1f) : 0f;
                }
        });
        WriteFloats(System.IO.Path.Combine(folder, "lake_natural.bin"), natural);
        WriteFloats(System.IO.Path.Combine(folder, "lake_carved.bin"), heights);
        WriteFloats(System.IO.Path.Combine(folder, "lake_delta.bin"), delta);
        WriteFloats(System.IO.Path.Combine(folder, "lake_wet.bin"), wet);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "lake_meta.txt"), FormattableString.Invariant($"size={size}\npixel={pixel}\nlake={pick}\nlevel={lake.Level}\ncells={lake.CellCount}\ninlets={lake.InletCount}\noutlet_stem={lake.OutletStem}\n"));
        Detail($"lake picture: lake {pick}, level {lake.Level:F0} m, {lake.CellCount} cells, {lake.InletCount} inlets, outlet stem {lake.OutletStem}, window {size * pixel / 1000:F1} km at {pixel:F1} m per pixel");
    }


    /// <summary>1000 x 1000 pixels of <paramref name="pixel"/> metres, sampled through the height model with and without the graph, lit from the top left of the picture with the same colour scale; rivers in blue after the carve.</summary>
    private void Render(string prefix, double centreAround, double centreAcross, double pixel)
    {
        const int size = 1000;
        var before = new float[(size + 2) * (size + 2)];
        var after = new float[before.Length];
        var wet = new bool[before.Length];
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, w =>
        {
            using TerrainGenerationHeightModel withRivers = NewModel();
            using var without = new TerrainGenerationHeightModel(_recipe, true);
            for (int row = w; row < size + 2; row += _workers)
                for (int col = 0; col < size + 2; col++)
                {
                    double around = centreAround + (col - 1 - size / 2) * pixel, across = centreAcross + (row - 1 - size / 2) * pixel;
                    int i = row * (size + 2) + col;
                    before[i] = (float)without.CalculateHeight(around, across);
                    TerrainGenerationHeightModel.Diagnostics d = withRivers.SampleDiagnostics(around, across);
                    after[i] = (float)d.Height;
                    wet[i] = d.InlandWater == InlandWaterKind.River && d.Height < d.WaterLevel;
                }
        });
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in before) { lo = Math.Min(lo, h); hi = Math.Max(hi, h); }
        var combined = new byte[2 * size * size * 3];
        for (int pass = 0; pass < 2; pass++)
        {
            float[] h = pass == 0 ? before : after;
            var bytes = new byte[size * size * 3];
            for (int row = 0; row < size; row++)
                for (int col = 0; col < size; col++)
                {
                    int i = (row + 1) * (size + 2) + col + 1;
                    // Hillshade: sun from the north-west at 40 degrees, vertical exaggeration 3.
                    double dzdx = (h[i + 1] - h[i - 1]) / (2 * pixel) * 3d, dzdy = (h[i + size + 2] - h[i - size - 2]) / (2 * pixel) * 3d;
                    // The light comes from the top left of the picture (T2R-7 review: it came from the bottom right, which reads valleys as ridges).
                    double zenith = 50d * Math.PI / 180d, lx = -Math.Sin(zenith) * Math.Sqrt(0.5), ly = lx, lz = Math.Cos(zenith);
                    double shade = Math.Clamp((-dzdx * lx - dzdy * ly + lz) / Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1d), 0d, 1d);
                    double t = Math.Clamp((h[i] - lo) / Math.Max(1f, hi - lo), 0d, 1d);
                    double r = 0.35 + 0.45 * t, g = 0.55 + 0.15 * t - 0.2 * t * t, b = 0.30 + 0.45 * t * t;
                    double k = 0.25 + 0.95 * shade;
                    if (pass == 1 && wet[i]) { r = 0.08; g = 0.36; b = 0.62; k = 0.8 + 0.2 * shade; }
                    int o = (row * size + col) * 3, oc = (row * 2 * size + pass * size + col) * 3;
                    bytes[o] = (byte)Math.Clamp(r * k * 255d, 0, 255); bytes[o + 1] = (byte)Math.Clamp(g * k * 255d, 0, 255); bytes[o + 2] = (byte)Math.Clamp(b * k * 255d, 0, 255);
                    combined[oc] = bytes[o]; combined[oc + 1] = bytes[o + 1]; combined[oc + 2] = bytes[o + 2];
                }
            Image.CreateFromData(size, size, false, Image.Format.Rgb8, bytes).SavePng(prefix + (pass == 0 ? "_before.png" : "_after.png"));
        }
        Image.CreateFromData(2 * size, size, false, Image.Format.Rgb8, combined).SavePng(prefix + "_before_after.png");
        var wetBytes = new float[before.Length];
        for (int i = 0; i < wetBytes.Length; i++) wetBytes[i] = wet[i] ? 1f : 0f;
        WriteFloats(prefix + "_natural.bin", before); WriteFloats(prefix + "_carved.bin", after); WriteFloats(prefix + "_wet.bin", wetBytes);
        Detail($"picture {prefix}_before_after.png (left: natural, right: carved; {pixel:F0} m per pixel; elevation scale {lo:F0} to {hi:F0} m)");
    }
}
