using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-10 (docs/work-orders/terrain-vegetation/T2.md, S9): inland water kind and float level in the runtime sampler, on a 400 km strip graph. Checks: along 50 reaches and 20 lakes the sampler's kind and level equal
/// the operator's within 1e-4 m (plus float rounding); the full, fused, height-only and lattice sample paths agree; river mouths are River down to the natural coastline and Ocean beyond (never a River below 0);
/// edits below the level stay wet and above are dry; the v12 cache record round-trips, a stale-version file is refused, and generation 1 keeps its v11 record size; grass and tree masks exclude water, including a bed
/// 0.3 m under a level with the same floor; 100,000 generation-1 points give no Lake or River. Negative control: a fabricated sample with the level below the bed is rejected by the validity rule the real samples
/// pass. Prints INLAND_WATER_SAMPLER=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests inland_water_sampler_test.
/// </summary>
public partial class InlandWaterSamplerTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 400;
    private readonly List<string> _failures = new();
    private TerrainGenerationParameters _recipe = null!;
    private VoxelTerrainSettings _settings = null!;
    private RingTerrainSampler _sampler = null!;
    private TerrainGenerationHeightModel _model = null!;
    private RiverGraph _graph = null!;
    private RiverNetwork _net = null!;
    private readonly List<(long A, int Z)> _points = new();

    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("INLAND_WATER_SAMPLER_DETAIL " + text);

    /// <summary>The S9 validity rule: an inland flag needs an inland kind, a finite level whose floor is the whole-metre surface, and a bed under the level.</summary>
    private static bool Valid(in TerrainSample s, float bed)
        => !s.HasExactWaterLevel
            || ((s.WaterKind == SurfaceWaterKind.Lake || s.WaterKind == SurfaceWaterKind.River)
                && float.IsFinite(s.ExactWaterLevel)
                && s.WaterSurfaceHeight == Mathf.FloorToInt(s.ExactWaterLevel)
                && bed < s.ExactWaterLevel);

    public int Run()
    {
        string root = ProjectSettings.GlobalizePath("user://t2r10_inland_water_sampler_test");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            _recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
            _settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, _recipe);
            int workers = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            RiverTestGraphs.Install(_recipe, _settings, StripStart, StripKm, workers);
            Detail($"strip graph installed in {clock.Elapsed.TotalSeconds:F1} s");
            _sampler = new RingTerrainSampler(_settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            _graph = _sampler.RuntimeRiverGraph!;
            _net = _graph.Network;
            _model = new TerrainGenerationHeightModel(_recipe, true, _graph);
            Check(_sampler.HasInlandWater, "a rivers sampler reports no inland water");
            KindsAndLevels();
            Paths();
            Mouths();
            Edits();
            Cache(Path.Combine(root, "lod_cache", "a.bin"));
            Masks();
            GenerationOne();
            Negative();
        }
        catch (Exception e) { Console.Error.WriteLine("INLAND_WATER_SAMPLER=FAIL exception " + e); return 1; }
        finally
        {
            _model?.Dispose();
            _sampler?.Dispose();
            RiverGraphStore.Clear();
            _settings?.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("INLAND_WATER_SAMPLER=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("INLAND_WATER_SAMPLER=PASS");
        return 0;
    }

    private static long Even(double v) => (long)Math.Round(v / 2d) * 2L;

    private bool Inside(double around, double across)
    {
        double min = StripStart + 20_000, max = StripStart + StripKm * 1000d - 20_000;
        return around > min && around < max && across > 3000 && across < _net.WidthM - 3000;
    }

    // ------------------------------------------------------------------ kinds and levels

    private void KindsAndLevels()
    {
        int wetReach = 0, wetLakePoints = 0, lakesWet = 0, pointsChecked = 0, lakeKind = 0, riverKind = 0;
        void CheckPoint(long a, int z, string label)
        {
            TerrainGenerationHeightModel.Diagnostics d = _model.SampleDiagnostics(a, z);
            TerrainSample s = _sampler.GetTerrainSample(a, z);
            float bed = (float)d.Height;
            bool wet = d.InlandWater != InlandWaterKind.None && double.IsFinite(d.WaterLevel) && bed < (float)d.WaterLevel;
            pointsChecked++;
            Check(Valid(s, bed), $"{label} ({a},{z}): the sample is not valid (kind {s.WaterKind}, level {s.ExactWaterLevel}, flag {s.HasExactWaterLevel}, bed {bed})");
            if (!wet)
            {
                Check(!s.HasExactWaterLevel, $"{label} ({a},{z}): flagged inland water where the operator has none");
                Check(s.WaterKind != SurfaceWaterKind.Lake && s.WaterKind != SurfaceWaterKind.River, $"{label} ({a},{z}): inland kind without operator water");
                return;
            }
            SurfaceWaterKind kind = d.InlandWater == InlandWaterKind.Lake ? SurfaceWaterKind.Lake : SurfaceWaterKind.River;
            if (kind == SurfaceWaterKind.Lake) lakeKind++; else riverKind++;
            Check(s.WaterKind == kind && s.HasExactWaterLevel, $"{label} ({a},{z}): kind {s.WaterKind}/{s.HasExactWaterLevel}, operator {d.InlandWater}");
            double tolerance = 1e-4 + Math.Abs(d.WaterLevel) * 6e-8;
            Check(Math.Abs(s.ExactWaterLevel - d.WaterLevel) <= tolerance, $"{label} ({a},{z}): level {s.ExactWaterLevel:R} vs operator {d.WaterLevel:R}");
            Check(s.WaterSurfaceHeight == (int)Math.Floor(s.ExactWaterLevel) && Math.Abs(s.WaterLevel - s.ExactWaterLevel) == 0f, $"{label} ({a},{z}): floor or WaterLevel property");
            _points.Add((a, z));
        }

        // 50 reaches, hashed, mid-reach on usable ground.
        int picked = 0;
        foreach (int r in Enumerable.Range(0, _net.Reaches.Length).OrderBy(r => RiverHash.Mix(Seed, r, 91)))
        {
            RiverReach reach = _net.Reaches[r];
            if (reach.SEnd - reach.SStart < 150f) continue;
            _net.Centre(reach.Stem, 0.5 * (reach.SStart + reach.SEnd), out double around, out double across, out _, out _);
            if (!Inside(around, across)) continue;
            long a = Even(around); int z = (int)Even(across);
            int before = _points.Count;
            CheckPoint(a, z, "reach " + r);
            if (_points.Count > before) wetReach++;
            // dry points beside the channel too: the bank and the valley floor
            CheckPoint(a, z + 60, "reach " + r + " +60");
            CheckPoint(a + 60, z, "reach " + r + " +60a");
            if (++picked == 50) break;
        }
        Check(picked == 50 && wetReach >= 40, $"reach picks: {picked} picked, {wetReach} with water at the centreline (want 50 and at least 40)");

        // 20 lakes: up to 12 hashed flooded cells each.
        double step = _graph.AroundStepMeters, acrossStep = _graph.AcrossStepMeters;
        var byLake = new Dictionary<int, List<LakeRun>>();
        foreach (LakeRun run in _graph.Runs) { if (!byLake.TryGetValue(run.LakeId, out List<LakeRun>? list)) byLake[run.LakeId] = list = new List<LakeRun>(); list.Add(run); }
        int lakesPicked = 0;
        foreach (int id in byLake.Keys.OrderBy(i => RiverHash.Mix(Seed, i, 92)))
        {
            RiverLake lake = _graph.Lakes[id];
            if (lake.CellCount < 6 || lake.MinRow < 4 || lake.MaxRow > _graph.Rows - 5 || !Inside(lake.MinCol * step, 10_000) || !Inside(lake.MaxCol * step, 10_000)) continue;
            int before = _points.Count, wetHere = 0;
            int k = 0;
            foreach (LakeRun run in byLake[id].OrderBy(x => RiverHash.Mix(Seed, id, x.Row * 100000L + x.Col)))
            {
                int c = run.Col + (int)(RiverHash.Mix(Seed, id, run.Row) % (ulong)run.Length);
                long a = Even((c + 0.5) * step); int z = (int)Even((run.Row + 0.5) * acrossStep);
                int pre = _points.Count;
                CheckPoint(a, z, "lake " + id);
                if (_points.Count > pre) wetHere++;
                if (++k == 12) break;
            }
            wetLakePoints += wetHere;
            if (wetHere > 0) lakesWet++;
            if (++lakesPicked == 20) break;
        }
        Check(lakesPicked == 20 && lakesWet >= 14, $"lake picks: {lakesPicked} picked, {lakesWet} with water at a flooded cell centre (want 20 and at least 14)");
        Check(lakeKind > 0 && riverKind > 0, $"the picks produced {lakeKind} Lake and {riverKind} River wet samples (want both kinds)");
        Detail($"kinds: {pointsChecked} points checked, wet {_points.Count} (River {riverKind}, Lake {lakeKind}); reaches wet {wetReach}/50, lakes wet {lakesWet}/20 ({wetLakePoints} flooded-cell hits)");
    }

    // ------------------------------------------------------------------ the four paths

    private void Paths()
    {
        var extra = new List<(long, int)>();
        foreach ((long a, int z) in _points.Take(120)) { extra.Add((a + 1, z)); extra.Add((a, z + 1)); extra.Add((a + 3, z - 5)); }
        int compared = 0;
        foreach ((long a, int z) in _points.Concat(extra.Select(e => (e.Item1, e.Item2))))
        {
            TerrainSample full = _sampler.GetTerrainSample(a, z);
            _sampler.GetRuntimeTerrainSampleAndPresentationHeight(a, z, out TerrainSample fused, out float fusedHeight);
            _sampler.GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(a, z, out TerrainSample heightOnly, out float heightOnlyHeight);
            bool same = full.WaterKind == fused.WaterKind && full.WaterKind == heightOnly.WaterKind
                && full.WaterSurfaceHeight == fused.WaterSurfaceHeight && full.WaterSurfaceHeight == heightOnly.WaterSurfaceHeight
                && full.HasExactWaterLevel == fused.HasExactWaterLevel && full.HasExactWaterLevel == heightOnly.HasExactWaterLevel
                && full.ExactWaterLevel == fused.ExactWaterLevel && full.ExactWaterLevel == heightOnly.ExactWaterLevel
                && full.SurfaceHeight == fused.SurfaceHeight && full.SurfaceHeight == heightOnly.SurfaceHeight
                && fusedHeight == heightOnlyHeight;
            Check(same, $"({a},{z}): the full, fused and height-only paths disagree ({full.WaterKind}/{fused.WaterKind}/{heightOnly.WaterKind}, {full.ExactWaterLevel}/{fused.ExactWaterLevel}/{heightOnly.ExactWaterLevel})");
            if ((a & 1L) == 0 && (z & 1) == 0)
            {
                _sampler.GetPresentationSurfaceHeightAndBiomeWeights(a, z, out float latticeHeight, out RingTerrainSampler.BiomeTextureWeights weights);
                Check(latticeHeight == fusedHeight && weights.Equals(_sampler.GetBiomeTextureWeights(full)), $"({a},{z}): the lattice path disagrees with the full path");
            }
            compared++;
        }
        Detail($"paths: {compared} points compared across full, fused, height-only and lattice");
    }

    // ------------------------------------------------------------------ river mouths

    private void Mouths()
    {
        int sea = 0, river = 0, ocean = 0, flankOcean = 0, floodedFlank = 0;
        double lowestRiverLevel = double.MaxValue;
        foreach (int i in Enumerable.Range(0, _net.Stems.Length).Where(i => _net.Stems[i].Terminal == RiverTerminal.Sea).OrderBy(i => RiverHash.Mix(Seed, i, 93)))
        {
            double length = _net.Curves[i].Length;
            if (length < 700) continue;
            _net.Centre(i, length, out double ea, out double ez, out _, out _);
            if (!Inside(ea, ez)) continue;
            sea++;
            for (double s = length - 600; s <= length; s += 25)
            {
                // T2R-7d (S13.1): beside the channel no Ocean sample stands on natural ground at or above 0 (the carve never takes a flank below sea level).
                _net.CentreFrame(i, s, out double ca, out double cz, out double nx, out double ny);
                for (double x = -300d; x <= 300d; x += 6d)
                {
                    long fa = Even(ca + x * nx); int fz = (int)Even(cz + x * ny);
                    if (_sampler.GetTerrainSample(fa, fz).WaterKind != SurfaceWaterKind.Ocean) continue;
                    flankOcean++;
                    if (_model.SampleNaturalDiagnostics(fa, fz).Height >= 0d) { floodedFlank++; Check(false, $"mouth stem {i} s {s} x {x}: an Ocean sample on natural ground at or above 0 ({fa}, {fz})"); }
                }
                _net.Centre(i, s, out double around, out double across, out _, out _);
                long a = Even(around); int z = (int)Even(across);
                TerrainSample sample = _sampler.GetTerrainSample(a, z);
                float bed = _sampler.GetPresentationSurfaceHeight(a, z);
                if (sample.WaterKind == SurfaceWaterKind.River)
                {
                    river++;
                    double natural = _model.SampleNaturalDiagnostics(a, z).Height; // the operator owns water only where the natural ground is at or above 0; the carved bed may dip under it
                    Check(natural >= 0d && sample.ExactWaterLevel >= -1e-3f, $"mouth stem {i} s {s}: a River sample beyond the natural coastline (natural {natural}, level {sample.ExactWaterLevel})");
                    lowestRiverLevel = Math.Min(lowestRiverLevel, sample.ExactWaterLevel);
                }
                else if (sample.WaterKind == SurfaceWaterKind.Ocean)
                {
                    ocean++;
                    Check(sample.WaterSurfaceHeight == 0 && !sample.HasExactWaterLevel && bed < 0f, $"mouth stem {i} s {s}: Ocean must stand at 0 with no exact level and a bed under 0");
                }
            }
            if (sea == 12) break;
        }
        Check(sea >= 1, "the strip has no usable sea-terminal stem to check a river mouth");
        Detail($"mouths: {sea} sea stems, {river} River and {ocean} Ocean samples in their last 600 m, lowest River level {lowestRiverLevel:F3}; flank Ocean samples within 300 m {flankOcean}, on natural ground at or above 0: {floodedFlank}");
    }

    // ------------------------------------------------------------------ edits

    private void Edits()
    {
        (long a, int z) = _points.First(p => (_sampler.GetTerrainSample(p.A, p.Z) is { HasExactWaterLevel: true } s) && s.ExactWaterLevel - _sampler.GetPresentationSurfaceHeight(p.A, p.Z) > 0.5f);
        TerrainSample plain = _sampler.GetTerrainSample(a, z);
        float level = plain.ExactWaterLevel;
        RingLodSurfaceEditSnapshot Edit(float height) => new(1, EmitterSiteTestWorlds.Circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>(),
            new Dictionary<RingTerrainColumnCoordinate, float> { [new RingTerrainColumnCoordinate(a, z)] = height }, true);
        TerrainSample below = Edit(level - 0.3f).GetPresentationTerrainSample(_sampler, a, z);
        Check(below.HasExactWaterLevel && below.WaterKind == plain.WaterKind && below.ExactWaterLevel == level && below.SurfaceHeight == Mathf.FloorToInt(level - 0.3f), $"an edit below the level did not stay wet (kind {below.WaterKind}, flag {below.HasExactWaterLevel}, surface {below.SurfaceHeight})");
        TerrainSample above = Edit(level + 0.5f).GetPresentationTerrainSample(_sampler, a, z);
        Check(!above.HasExactWaterLevel && above.WaterKind == SurfaceWaterKind.None && above.SurfaceHeight == Mathf.FloorToInt(level + 0.5f), $"an edit above the level is not dry (kind {above.WaterKind}, flag {above.HasExactWaterLevel})");
        TerrainSample untouched = Edit(level - 0.3f).GetPresentationTerrainSample(_sampler, a + 2, z);
        Check(untouched.Equals(_sampler.GetTerrainSample(a + 2, z)), "an unedited column changed");
        Detail($"edits: level {level:F2}; below stays {below.WaterKind}, above is {above.WaterKind}");
    }

    // ------------------------------------------------------------------ cache record

    private void Cache(string path)
    {
        var edits = new RingLodSurfaceEditSnapshot(1, EmitterSiteTestWorlds.Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var points = _points.Take(300).Select(p => (p.A, p.Z)).ToList();
        string error = RingTerrainProxySector.RoundTripCacheRecords(path, edits, _sampler, EmitterSiteTestWorlds.Width, points, out int version, out long payload, out bool staleLoaded);
        Check(error.Length == 0, "the v13 record did not round-trip: " + error);
        Check(version == 13, $"a rivers world wrote cache version {version}, want 13");
        Check(payload == 4 + 68L * points.Count, $"v13 payload {payload} bytes, want {4 + 68L * points.Count}");
        Check(!staleLoaded, "a stale-version file was accepted");
        using VoxelTerrainSettings gen1Settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(gen1Settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        string path1 = Path.Combine(Path.GetDirectoryName(path)!, "gen1", "a.bin");
        string error1 = RingTerrainProxySector.RoundTripCacheRecords(path1, edits, gen1, EmitterSiteTestWorlds.Width, points, out int version1, out long payload1, out bool stale1);
        Check(error1.Length == 0, "the generation-1 record did not round-trip: " + error1);
        Check(version1 == 11 && payload1 == 4 + 61L * points.Count && !stale1, $"generation-1 cache is not the v11 layout (version {version1}, payload {payload1}, want {4 + 61L * points.Count}, stale accepted {stale1})");
        Detail($"cache: v{version} {payload} bytes for {points.Count} records; generation 1 v{version1} {payload1} bytes");
    }

    // ------------------------------------------------------------------ masks

    private void Masks()
    {
        int checkedWet = 0;
        foreach ((long a, int z) in _points.Take(200))
        {
            TerrainSample s = _sampler.GetTerrainSample(a, z);
            float bed = _sampler.GetPresentationSurfaceHeight(a, z);
            Check(RingGrassTileBuilder.IsWaterCovered(s, bed), $"({a},{z}): the grass mask does not exclude water (bed {bed}, level {s.ExactWaterLevel})");
            Check(RingBiomeDecorationLayer.IsSurfaceWater(new RingLodSurfaceEditSnapshot(1, EmitterSiteTestWorlds.Circumference, new Dictionary<RingTerrainColumnCoordinate, int>()), _sampler, s, bed), $"({a},{z}): the tree mask does not exclude water");
            checkedWet++;
        }
        // The shallow case: a bed 0.3 m under a level with the same floor (the whole-metre rule would call it dry).
        var shallow = new TerrainSample(100, default, 0f, 0f, 0f, 0f, 0f, default, SurfaceWaterKind.River, 100, default, 100.6f, true);
        Check(RingGrassTileBuilder.IsWaterCovered(shallow, 100.3), "grass grows in water 0.3 m deep");
        Check(!RingGrassTileBuilder.IsWaterCovered(shallow, 100.6), "grass is excluded at the waterline itself");
        Check(shallow.ResolveDisplayedHeight(100.3f) == 100.6f && shallow.ResolveDisplayedHeight(100.7f) == 100.7f, "the displayed height is not the exact level");
        Detail($"masks: {checkedWet} wet samples excluded; shallow 0.3 m case excluded");
    }

    // ------------------------------------------------------------------ generation 1

    private void GenerationOne()
    {
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        Check(!gen1.HasInlandWater, "a generation-1 sampler reports inland water");
        int inland = 0, flagged = 0, n = 0, ocean = 0;
        for (int i = 0; i < 100_000; i++)
        {
            long a = Even(RiverHash.Mix(Seed, i, 94) % (ulong)EmitterSiteTestWorlds.Circumference);
            int z = (int)Even(RiverHash.Mix(Seed, i, 95) % (ulong)(EmitterSiteTestWorlds.Width - 2));
            gen1.GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(a, z, out TerrainSample s, out _);
            if (s.WaterKind is SurfaceWaterKind.Lake or SurfaceWaterKind.River) inland++;
            if (s.HasExactWaterLevel) flagged++;
            if (s.WaterKind == SurfaceWaterKind.Ocean) ocean++;
            n++;
        }
        Check(inland == 0 && flagged == 0, $"generation 1 gave {inland} Lake or River and {flagged} flagged samples in {n}");
        Detail($"generation 1: {n} lattice points, {ocean} Ocean, {inland} Lake or River, {flagged} flagged");
    }

    // ------------------------------------------------------------------ negative control

    private void Negative()
    {
        // A fabricated sample whose level is below the bed is not a valid inland sample; the real samples pass the same rule (KindsAndLevels).
        var fabricated = new TerrainSample(50, default, 0f, 0f, 0f, 0f, 0f, default, SurfaceWaterKind.River, 49, default, 49.5f, true);
        Check(!Valid(fabricated, 50.2f), "NEGATIVE CONTROL FAILED: a level below the bed was accepted");
        Check(Valid(fabricated, 49.2f), "NEGATIVE CONTROL FAILED: the validity rule rejects a good sample");
        var (kind, _, _, flag) = RingTerrainSampler.ClassifyRuntimeWater(true, InlandWaterKind.River, 49.5, 50.2f, 50);
        Check(kind == SurfaceWaterKind.None && !flag, "the classifier made water where the level is below the bed");
        var wrongFloor = fabricated with { WaterSurfaceHeight = 48 };
        Check(!Valid(wrongFloor, 49.2f), "NEGATIVE CONTROL FAILED: a surface that is not the floor of the level was accepted");
    }
}
