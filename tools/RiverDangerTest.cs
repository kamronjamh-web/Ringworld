using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-3 (docs/work-orders/terrain-vegetation/T3.md, Spec E1 to E4): spans, calibrated hydraulics, danger classes, landings and exclusions in the bake, on the 2,000 km D4 strip
/// (start 9,804 km, CPU source) for seeds 1337 and 4242. Checks: class shares within 3 points of the strip's own production row (Spec E3), with at least 70 % calm or swift and at most
/// 3 % torrent. The user's bands (with fierce 5-10 %) are a whole-ring property and are checked on the whole-ring report (T3R-3 escalation decisions, T3.md Review notes): this strip
/// held 10.1 % and 5.9 % fierce on the two seeds before T3R-3 changed anything. Also: the danger speed is monotone in gradient and in discharge; at most 5 km of dangerous water follows any
/// dangerous span; every landing and every first calm or swift span after a dangerous one has a valley slope of at most 0.7; no fierce or torrent span inside the exclusion distances;
/// widths above 0 and at most 400 m, depth above 0 and Q = U w d within 1e-6; classes and landings equal across two bakes, two worker counts and a file round trip. Negative controls: the interim law
/// fails the share check, a fabricated 8 km torrent stem fails the exit check, and each other check is shown to fail on a fabricated input. Prints RIVER_DANGER=PASS|FAIL.
/// Headless: tools/run_tests.ps1 -Tests river_danger_test.
/// </summary>
public partial class RiverDangerTest : RefCounted
{
    private const double StripStartMeters = 9_804_000d;
    private const int StripKilometres = 2000;
    private const double BandShareTolerance = 0.03;

    /// <summary>
    /// The strip's own production row (calm, swift, fierce, torrent after landings and exclusions) with the E2 and E3 constants on the stored spans, recorded in Spec E3 on 2026-11-12
    /// (T3R-3 escalation decisions). A change of more than 3 points means the law, the constants or the spans changed: re-record it with the Spec, do not widen the tolerance.
    /// </summary>
    private static readonly (int Seed, double[] Baseline)[] Cases =
    {
        (1337, new[] { 0.325, 0.552, 0.104, 0.019 }),
        (4242, new[] { 0.469, 0.460, 0.066, 0.005 }),
    };

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("RIVER_DANGER_DETAIL " + text);

    public int Run()
    {
        try
        {
            CheckMonotone();
            CheckControlsFabricated();
            foreach ((int seed, double[] baseline) in Cases) Strip(seed, baseline);
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_DANGER=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_DANGER=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("RIVER_DANGER=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ the checks, pure over arrays so the controls can feed them fabricated input

    /// <summary>
    /// Violations of the strip's own row (3 points per class) and of the two limits that hold in any region (at least 70 % calm or swift, at most 3 % torrent);
    /// <paramref name="share"/> is calm, swift, fierce, torrent. The fierce band of 5 to 10 % is a whole-ring property (<see cref="RiverDangerReport.InUserBands"/>).
    /// </summary>
    private static int ShareViolations(double[] share, double[] baseline)
    {
        int bad = 0;
        if (share[0] + share[1] < 0.70) bad++;
        if (share[3] > 0.03) bad++;
        for (int k = 0; k < 4; k++) if (Math.Abs(share[k] - baseline[k]) > BandShareTolerance) bad++;
        return bad;
    }

    /// <summary>Dangerous spans with more than 5,000 m of dangerous water after their downstream end.</summary>
    private static int ExitViolations(int[] successor, bool[] dangerous, double[] length)
    {
        double[] after = RiverSpanTable.DangerousRunAfter(successor, dangerous, length);
        int bad = 0;
        for (int i = 0; i < after.Length; i++) if (dangerous[i] && after[i] > RiverSpanTable.RunLimitMeters) bad++;
        return bad;
    }

    /// <summary>Landings and first calm or swift spans after a dangerous one whose valley slope exceeds 0.7.</summary>
    private static int ValleyViolations(int[] successor, bool[] dangerous, bool[] landing, float[] slope)
    {
        var afterDangerous = new bool[successor.Length];
        for (int i = 0; i < successor.Length; i++) if (dangerous[i] && successor[i] >= 0) afterDangerous[successor[i]] = true;
        int bad = 0;
        for (int i = 0; i < successor.Length; i++)
            if ((landing[i] || (afterDangerous[i] && !dangerous[i])) && slope[i] > 0.7f) bad++;
        return bad;
    }

    /// <summary>Spans of class fierce or torrent with a base-curve point (sampled every 37 m, off the builder's 50 m grid) more than 5 m inside an exclusion zone.</summary>
    private static int ExclusionViolations(RiverNetwork net, RiverSpanTable table, RiverExclusions zones)
    {
        int bad = 0;
        for (int i = 0; i < table.Count; i++)
        {
            if (table.Class[i] < RiverClass.Fierce) continue;
            RiverStemCurve curve = net.Curves[net.Reaches[table.Spans[i].Reach].Stem];
            double s0 = table.Spans[i].SStart, s1 = table.Spans[i].SEnd;
            for (double s = s0 + 13d; s < s1; s += 37d)
            {
                curve.Eval(s, out double x, out double y, out _, out _);
                if (zones.Clearance(curve.OriginAround + x, curve.OriginAcross + y) < -5d) { bad++; break; }
            }
        }
        return bad;
    }

    /// <summary>Spans outside the width, depth or continuity limits: width above 0 and at most 400 m, depth above 0, Q = U w d within 1e-6 (no floors, T2R-20).</summary>
    private static int SectionViolations(IReadOnlyList<RiverSpan> spans, float[] halfWidth, float[] depth, float[] speed)
    {
        int bad = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            double w = 2d * halfWidth[i], q = spans[i].Q;
            bool ok = w > 0d && w <= 400d + 1e-3 && depth[i] > 0d && !double.IsNaN(w) && !float.IsNaN(depth[i]) && !float.IsNaN(speed[i]) && (q == 0d || Math.Abs(speed[i] * w * depth[i] - q) <= 1e-6 * q);
            if (!ok) bad++;
        }
        return bad;
    }

    /// <summary>Spans whose class or landing differs between two tables.</summary>
    private static int DeterminismViolations(RiverSpanTable a, RiverSpanTable b)
    {
        if (a.Count != b.Count) return Math.Max(1, Math.Abs(a.Count - b.Count));
        int bad = 0;
        for (int i = 0; i < a.Count; i++)
            if (a.Class[i] != b.Class[i] || a.Spans[i].Landing != b.Spans[i].Landing || a.Spans[i].Q != b.Spans[i].Q || a.Spans[i].Sh != b.Spans[i].Sh || a.Spans[i].SStart != b.Spans[i].SStart) bad++;
        return bad;
    }

    /// <summary>Grid points where the danger speed falls as the gradient or the discharge rises (Q 10 to 20,000, gradient 1e-4 to 0.3, three ferocities).</summary>
    private static int MonotoneViolations(Func<double, double, double, double> vd)
    {
        const int steps = 40;
        double[] q = Enumerable.Range(0, steps).Select(i => 10d * Math.Pow(2000d, i / (steps - 1d))).ToArray();
        double[] g = Enumerable.Range(0, steps).Select(i => 1e-4 * Math.Pow(3000d, i / (steps - 1d))).ToArray();
        int bad = 0;
        foreach (double fer in new[] { 0.6, 1d, 1.5 })
            for (int i = 0; i < steps; i++)
                for (int j = 0; j < steps; j++)
                {
                    double v = vd(q[i], g[j], fer);
                    if (j > 0 && v < vd(q[i], g[j - 1], fer) - 1e-9) bad++;
                    if (i > 0 && v < vd(q[i - 1], g[j], fer) - 1e-9) bad++;
                }
        return bad;
    }

    private static double DangerSpeedOf(double q, double g, double fer)
    {
        RiverDangerConstants c = RiverDangerConstants.Default;
        RiverDangerChannel ch = RiverDanger.Channel(c, q, g, 1d, fer);
        return RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, g, fer);
    }

    private void CheckMonotone()
    {
        int bad = MonotoneViolations(DangerSpeedOf);
        Check(bad == 0, $"the danger speed is not monotone in gradient and discharge ({bad} grid points)");
        Detail($"monotone grid: {bad} violations; control (a speed that falls with discharge): {MonotoneViolations((q, g, f) => 1000d / DangerSpeedOf(q, g, f))} violations");
        Check(MonotoneViolations((q, g, f) => 1000d / DangerSpeedOf(q, g, f)) > 0, "negative control: a speed that falls with discharge was not flagged");
    }

    /// <summary>The controls that need no strip: each check must fail on an input built to break it.</summary>
    private void CheckControlsFabricated()
    {
        // Exit: a stem of eight dangerous kilometre spans and no exit.
        int[] chain = Enumerable.Range(0, 8).Select(i => i < 7 ? i + 1 : -1).ToArray();
        int exitBad = ExitViolations(chain, Enumerable.Repeat(true, 8).ToArray(), Enumerable.Repeat(1000d, 8).ToArray());
        Check(exitBad > 0, "negative control: 8 km of torrent without an exit was not flagged");
        int exitOk = ExitViolations(new[] { 1, 2, 3, 4, -1 }, new[] { true, true, true, true, false }, Enumerable.Repeat(1000d, 5).ToArray());
        Check(exitOk == 0, "the exit check flagged 4 km of dangerous water followed by a calm span");
        // Valley: a landing with a 1.2 slope, and a first span after a dangerous one with a 0.9 slope.
        Check(ValleyViolations(new[] { 1, -1 }, new[] { true, false }, new[] { false, false }, new[] { 0.2f, 0.9f }) == 1, "negative control: a steep first span after a dangerous one was not flagged");
        Check(ValleyViolations(new[] { -1 }, new[] { false }, new[] { true }, new[] { 1.2f }) == 1, "negative control: a steep landing was not flagged");
        Check(ValleyViolations(new[] { 1, -1 }, new[] { false, false }, new[] { false, false }, new[] { 1.5f, 1.5f }) == 0, "the valley check flagged a span that is neither a landing nor an exit");
        // Section: one span that is valid, then one broken in a single way each (width, width, depth, Q = U w d).
        var q30 = new[] { new RiverSpan { Q = 30f } };
        Check(SectionViolations(q30, new[] { 20f }, new[] { 1.5f }, new[] { 0.5f }) == 0, "the section check flagged a valid span");
        Check(SectionViolations(q30, new[] { 3f }, new[] { 1.0f }, new[] { 30f / (6f * 1.0f) }) == 0, "the section check flagged a 6 m wide, 1 m deep span (no floors since T2R-20)");
        Check(SectionViolations(q30, new[] { 0f }, new[] { 1.5f }, new[] { 0.5f }) == 1, "negative control: a zero-width span was not flagged");
        Check(SectionViolations(q30, new[] { 210f }, new[] { 1.5f }, new[] { 30f / (420f * 1.5f) }) == 1, "negative control: a 420 m wide span was not flagged");
        Check(SectionViolations(q30, new[] { 20f }, new[] { 0f }, new[] { 0.5f }) == 1, "negative control: a zero-depth span was not flagged");
        Check(SectionViolations(q30, new[] { 20f }, new[] { 1.5f }, new[] { 0.51f }) == 1, "negative control: a span with a wrong Q = U w d was not flagged");
        // The user's bands (whole ring): the recorded whole-ring rows of Spec E3 are inside; too little fierce, too much fierce and too much torrent are flagged.
        Check(RiverDangerReport.InUserBands(new[] { 0.390, 0.507, 0.089, 0.013 }) && RiverDangerReport.InUserBands(new[] { 0.413, 0.493, 0.083, 0.011 }), "the whole-ring rows of Spec E3 are outside the user's bands");
        Check(!RiverDangerReport.InUserBands(new[] { 0.50, 0.45, 0.049, 0.001 }) && !RiverDangerReport.InUserBands(new[] { 0.40, 0.47, 0.11, 0.02 }) && !RiverDangerReport.InUserBands(new[] { 0.40, 0.48, 0.08, 0.04 }),
            "negative control: a mix outside the user's bands was not flagged");
    }

    // ------------------------------------------------------------------ the strip

    private sealed class Baked
    {
        public RiverNetwork Net = null!;
        public RiverSpanTable Table = null!;
        public DrainageField Field = null!;
        public DrainageGrid Grid = null!;
        public double Seconds;
    }

    private static Baked Bake(TerrainGenerationParameters recipe, IDrainageHeightSource source, DrainageGrid grid, int workers)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrainageField field = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None);
        RiverNetwork net = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { Source = source, Workers = workers }, CancellationToken.None, out _);
        return new Baked { Net = net, Table = net.SpanTable!, Field = field, Grid = grid, Seconds = clock.Elapsed.TotalSeconds };
    }

    private void Strip(int seed, double[] baseline)
    {
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(seed, 1, new[] { "water.rivers" });
        int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(seed, recipe);
        var source = new CpuDrainageHeightSource(recipe, HydrologyBakeInputs.Capture(settings, recipe, workers).Climate, workers);
        DrainageGridSpec spec = DrainageGridSpec.Strip(recipe, StripStartMeters, StripKilometres);
        DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
        Baked a = Bake(recipe, source, grid, workers);
        RiverNetwork net = a.Net;
        RiverSpanTable t = a.Table;
        Check(t.Count > 1000, $"seed {seed}: only {t.Count} spans on the strip");

        // Shares by length after landings and exclusions.
        var share = new double[4];
        double total = 0d;
        // T2R-8b: the drowned channels through the lakes (flat, so calm) are not rivers to classify: spans inside the in-lake part of a stem are counted apart, so the bars measure the same rivers as before.
        double lakeMeters = 0d;
        for (int i = 0; i < t.Count; i++)
        {
            double len = t.Spans[i].SEnd - t.Spans[i].SStart;
            int spanStem = net.Reaches[t.Spans[i].Reach].Stem;
            if (net.InLakeEnd[spanStem] > 0d && t.Spans[i].SEnd <= net.InLakeEnd[spanStem] + 1e-3) { lakeMeters += len; continue; }
            share[(int)t.Class[i]] += len; total += len;
        }
        Detail($"seed {seed}: in-lake spans left out of the shares: {lakeMeters / 1000d:F0} km");
        for (int k = 0; k < 4; k++) share[k] /= total;
        int shareBad = ShareViolations(share, baseline);
        Check(shareBad == 0, $"seed {seed}: class shares {Mix(share)} are more than 3 points from the strip's production row {Mix(baseline)}, or under 70 % calm or swift, or over 3 % torrent");
        // Negative control: the interim law on the same strip must fail.
        RiverDangerSpans reaches = RiverDangerReport.Cut(net, double.PositiveInfinity);
        double[] interim = RiverDangerReport.Evaluate(net, reaches, RiverDangerConstants.Interim, false).Share;
        Check(ShareViolations(interim, baseline) > 0 && !RiverDangerReport.InUserBands(interim), $"seed {seed}: negative control: the interim law {Mix(interim)} was not flagged by the share check");

        // Exit rule, valleys, exclusions, sections.
        var length = new double[t.Count];
        for (int i = 0; i < t.Count; i++) length[i] = t.Spans[i].SEnd - t.Spans[i].SStart;
        int[] successor = t.Successors(net);
        bool[] dangerous = Enumerable.Range(0, t.Count).Select(t.IsDangerous).ToArray();
        bool[] landing = Enumerable.Range(0, t.Count).Select(i => t.Spans[i].Landing > 1f).ToArray();
        int exitBad = ExitViolations(successor, dangerous, length);
        Check(exitBad == 0, $"seed {seed}: {exitBad} dangerous spans have more than 5 km of dangerous water after them");
        int valleyBad = ValleyViolations(successor, dangerous, landing, t.ValleySlope);
        Check(valleyBad == 0, $"seed {seed}: {valleyBad} landings or exit spans have a valley slope above 0.7");
        RiverExclusions? zones = RiverExclusions.Build(recipe);
        Check(zones != null && zones.ZoneCount > 0, $"seed {seed}: the recipe has no exclusion zones");
        // The production grid's evaluation (the calibration tool) must reproduce the baked table with the default constants, and move with them.
        bool[]? flagged = zones?.Mark(net, t.Spans);
        double[] again0 = RiverDangerReport.StoredShares(net, flagged, RiverDangerConstants.Default).Share, rough = RiverDangerReport.StoredShares(net, flagged, RiverDangerConstants.Default with { Nm = 0.22, E0 = 0.5 }).Share;
        Check(Enumerable.Range(0, 4).All(k => Math.Abs(again0[k] - share[k]) < 1e-9), $"seed {seed}: the production grid gives {Mix(again0)} for the default constants, the baked table {Mix(share)}");
        Check(rough[2] + rough[3] < share[2] + share[3] - 0.005, $"seed {seed}: negative control: rougher mountain channels and a lower eta did not lower the dangerous share ({Mix(rough)})");
        int exclusionBad = zones == null ? 0 : ExclusionViolations(net, t, zones);
        Check(exclusionBad == 0, $"seed {seed}: {exclusionBad} fierce or torrent spans lie inside an exclusion distance");
        int sectionBad = SectionViolations(t.Spans, t.HalfWidth, t.Depth, t.Speed);
        Check(sectionBad == 0, $"seed {seed}: {sectionBad} spans break the width, depth or Q = U w d limits");
        // The straight strip anchors: the stored path and level parts are the bake's, so a loaded graph must answer the same.
        int dangerousCount = dangerous.Count(d => d);
        Detail($"seed {seed}: {t.Count} spans, {total / 1000d:F0} km, shares {Mix(share)} (production row {Mix(baseline)}), landings {t.LandingCount} spans {t.LandingMeters / 1000d:F0} km = {t.LandingMeters / total * 100:F2} %, dangerous spans {dangerousCount}, zones {zones?.ZoneCount}, bake {a.Seconds:F1} s");

        // Negative controls on the real strip.
        if (zones != null && dangerousCount > 0)
        {
            int pick = Enumerable.Range(0, t.Count).First(i => dangerous[i]);
            RiverStemCurve curve = net.Curves[net.Reaches[t.Spans[pick].Reach].Stem];
            curve.Eval(0.5 * (t.Spans[pick].SStart + t.Spans[pick].SEnd), out double x, out double y, out _, out _);
            RiverExclusions fabricated = RiverExclusions.Custom(net.CircumferenceM, new[] { (curve.OriginAround + x, curve.OriginAcross + y, 3000d) });
            Check(ExclusionViolations(net, t, fabricated) > 0, $"seed {seed}: negative control: a zone laid on a dangerous span was not flagged");
        }
        {
            // A table built without the exit rule keeps its long dangerous runs: the exit check must flag it.
            RiverSpan[] raw = t.Spans.Select(s => s with { Landing = 1f }).ToArray();
            RiverSpanTable unresolved = RiverSpanTable.Create(net, raw, null, false);
            bool[] danger = Enumerable.Range(0, unresolved.Count).Select(unresolved.IsDangerous).ToArray();
            int unresolvedBad = ExitViolations(unresolved.Successors(net), danger, length);
            Check(unresolvedBad > 0, $"seed {seed}: negative control: the strip without landings passed the exit check");
            Detail($"seed {seed}: control without landings: {unresolvedBad} dangerous spans with more than 5 km after them");
            RiverSpan[] shifted = (RiverSpan[])t.Spans.Clone();
            shifted[shifted.Length / 2].Landing = shifted[shifted.Length / 2].Landing > 1f ? 1f : 2f;
            Check(DeterminismViolations(t, RiverSpanTable.Create(net, shifted, null, false)) > 0, $"seed {seed}: negative control: a changed landing was not flagged by the determinism check");
        }

        // Two bakes and two worker counts give the same classes and landings.
        Baked again = Bake(recipe, source, grid, workers);
        Check(DeterminismViolations(t, again.Table) == 0, $"seed {seed}: two bakes differ in classes or landings");
        Baked one = Bake(recipe, source, grid, 1);
        Check(DeterminismViolations(t, one.Table) == 0, $"seed {seed}: 1 worker and {workers} workers differ in classes or landings");
        Detail($"seed {seed}: second bake {again.Seconds:F1} s, one-worker bake {one.Seconds:F1} s");

        // A file round trip: the loader derives the same table from the stored records.
        RiverGraph graph = RiverGraph.Build(net, a.Field, grid, 0x1234UL, 0);
        byte[] bytes = RiverGraphFormat.Write(graph);
        Check(RiverGraphFormat.TryRead(bytes, 0x1234UL, out RiverGraph? loaded, out string error) && loaded != null, $"seed {seed}: the graph did not read back: {error}");
        if (loaded?.Network.SpanTable is RiverSpanTable lt)
        {
            Check(DeterminismViolations(t, lt) == 0, $"seed {seed}: the loaded graph differs in classes or landings");
            int derived = 0;
            for (int i = 0; i < t.Count; i++)
                if (t.HalfWidth[i] != lt.HalfWidth[i] || t.Depth[i] != lt.Depth[i] || t.ValleySlope[i] != lt.ValleySlope[i] || t.DangerSpeed[i] != lt.DangerSpeed[i] || t.Sr[i] != lt.Sr[i]) derived++;
            Check(derived == 0, $"seed {seed}: {derived} spans derive different values after loading");
        }
        else Fail($"seed {seed}: the loaded graph has no spans");
    }

    private static string Mix(double[] s) => FormattableString.Invariant($"{s[0] * 100:F1}/{s[1] * 100:F1}/{s[2] * 100:F1}/{s[3] * 100:F1} %");
}
