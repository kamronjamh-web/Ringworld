using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-3 (docs/work-orders/terrain-vegetation/T6.md, design A1-A4): canopy structure in worlds with vegetation.forest_structure (bit 6). Source table against the catalog and the generation 1 ranges;
/// the structured path reads species and size from the table (no sapling in the canopy); size class follows stand age; species follow habitat; the treeline thins and dwarfs trees; the feature off
/// is byte-identical to generation 1 (including the fingerprint's eight tile hashes); the per-pose LOD triangle sums (escalation: LOD2 ratio above 1.10); determinism.
/// Prints FOREST_STRUCTURE=PASS. Headless: tools/run_tests.ps1 -Tests forest_structure_test.
/// </summary>
public partial class ForestStructureTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const string FeatureId = "vegetation.forest_structure";
    private static readonly long Cells = (long)Math.Ceiling(Circ / 30.0);
    private static readonly double AroundCell = Circ / Math.Ceiling(Circ / 30.0);
    private static readonly int[] SaplingVariants = { 0, 1, 2, 14, 24, 25, 26 };
    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 30) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("FOREST_STRUCTURE_DETAIL " + text);

    private readonly record struct Key(long A, long Z);
    private readonly record struct Rec(TreeSpecies Species, int Variant, float Height, float T, float M, float W, float R64, float X, float Age, double Rank, long Around, int Across, bool Dry, float PinePred, float Slope);

    private sealed class Ctx
    {
        public RingTerrainSampler S = null!;
        public ForestRegionSampler F = null!;
        public RingHabitatFieldGrid G = null!;
        public ForestStructureSampler Structure = null!;
    }

    private LandscapeWorld.Spec _specOn = null!, _specOff = null!;
    private RingTerrainSampler _rootOn = null!, _rootOff = null!;
    private IReadOnlyList<ImportedTreeVariant> _catalog = null!;
    private int[,] _triangles = null!;
    private LandscapePoseFinder.Fixture _fixture = null!;
    private readonly Dictionary<Key, List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>> _on = new(), _off = new();
    private readonly List<Key> _block = new(), _treeline = new(), _dry = new();
    private int _workers;
    private Dictionary<TreeSpecies, int> _maxRank = null!;

    public int Run()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Setup();
            TableChecks();
            SamplerChecks();
            BuildTiles();
            Detail($"tiles built in {clock.Elapsed.TotalSeconds:F1} s");
            Determinism();
            SourceChecks();
            List<Rec> recs = Analyse();
            Detail($"records analysed: {recs.Count} in {clock.Elapsed.TotalSeconds:F1} s");
            AgeCheck(recs);
            HabitatCheck(recs);
            TreelineCheck(recs);
            FeatureOff();
            Poses();
        }
        catch (Exception e) { Console.Error.WriteLine("FOREST_STRUCTURE=FAIL exception " + e); return 1; }
        finally { _rootOn?.Dispose(); _rootOff?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("FOREST_STRUCTURE=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("FOREST_STRUCTURE=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixtures

    private static Key TileOf(LandscapePose p)
        => new((long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), (long)Math.Floor(p.Across / 240.0));

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    private LandscapePose Pose(string name) => _fixture.Poses.First(p => p.Name == name);

    private void Setup()
    {
        _catalog = ImportedTreeCatalog.Load(); // main thread, before any worker
        _triangles = new int[_catalog.Count, 4];
        foreach (ImportedTreeVariant v in _catalog)
            for (int lod = 0; lod < 4; lod++)
            {
                Mesh mesh = v.Lods[lod];
                int tris = 0;
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    int indexLen = mesh is ArrayMesh am ? am.SurfaceGetArrayIndexLen(s) : 0;
                    tris += (indexLen > 0 ? indexLen : mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length) / 3;
                }
                _triangles[v.Index, lod] = tris;
            }
        _maxRank = ImportedTreeSourceTable.Rows.Where(x => x.Role == TreePlacementRole.Canopy).GroupBy(x => x.Species).ToDictionary(g => g.Key, g => g.Max(x => x.AgeRank));
        _specOn = LandscapeWorld.For(1, new[] { FeatureId });
        _specOff = LandscapeWorld.For(1);
        _rootOn = _specOn.CreateSampler();
        _rootOff = _specOff.CreateSampler();
        _workers = Math.Clamp(System.Environment.ProcessorCount - 1, 2, 8);
        _fixture = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        Check((_rootOn.WorldFeatureMask & (1 << 6)) != 0, "the review world lacks bit 6");
        Check((_rootOff.WorldFeatureMask & (1 << 6)) == 0, "generation 1 has bit 6");
        Detail($"worlds: on={_specOn.Tag} mask={_rootOn.WorldFeatureMask} off={_specOff.Tag} mask={_rootOff.WorldFeatureMask}; {_workers} workers; layout Circ/ceil(Circ/30), cell {AroundCell:F6} m");
    }

    // ------------------------------------------------------------------ a. table

    private void TableChecks()
    {
        var rows = ImportedTreeSourceTable.Rows.Take(30).ToList(); // the base rows; derived rows 30-42 are pinned by tree_catalog_append_test
        Check(ImportedTreeSourceTable.Rows.Count == 55 && ImportedTreeSourceTable.Rows.Skip(30).Take(13).All(r => r.Role == TreePlacementRole.DeadWood && r.EnablingFeature == FeatureId) && ImportedTreeSourceTable.Rows.Skip(43).All(r => r.Role == TreePlacementRole.Shrub && r.Form == TreeFormKind.Shrub && r.EnablingFeature == FeatureId), "table has " + ImportedTreeSourceTable.Rows.Count + " rows, expected 30 base + 13 derived (dead wood) + 12 shrubs, enabled by the feature");
        Check(_catalog.Count == 30, "catalog has " + _catalog.Count + " variants");
        var firByHeight = _catalog.Where(v => v.Species == TreeSpecies.Fir).OrderByDescending(v => v.HeightMeters).Select(v => v.Index).ToList();
        for (int i = 0; i < Math.Min(rows.Count, _catalog.Count); i++)
        {
            ImportedTreeSourceRow row = rows[i];
            ImportedTreeVariant v = _catalog[i];
            Check(row.Index == v.Index && row.Index == i, $"a. row {i} index {row.Index} vs catalog {v.Index}");
            Check(row.Species == v.Species, $"a. row {i} species {row.Species} vs catalog {v.Species}");
            Check(row.SourceName == v.Name, $"a. row {i} name '{row.SourceName}' vs catalog '{v.Name}'");
            TreeSpecies literal = i < 12 ? TreeSpecies.Maple : i < 15 ? TreeSpecies.Fir : TreeSpecies.Pine;
            Check(row.Species == literal, $"a. row {i} species {row.Species} vs the generation 1 literal range {literal}");
            TreeSizeClass expected;
            string name = v.Name.ToLowerInvariant();
            if (v.Species == TreeSpecies.Fir) expected = firByHeight.IndexOf(i) switch { 0 => TreeSizeClass.Large, 1 => TreeSizeClass.Medium, _ => TreeSizeClass.Small };
            else expected = name.Contains("sapling") ? TreeSizeClass.Sapling : name.Contains("small") ? TreeSizeClass.Small : name.Contains("medium") ? TreeSizeClass.Medium
                : name.Contains("big") ? TreeSizeClass.Big : TreeSizeClass.Large;
            Check(row.SizeClass == expected, $"a. row {i} ({v.Name}) class {row.SizeClass}, name/height says {expected}");
            bool understory = expected == TreeSizeClass.Sapling || (v.Species == TreeSpecies.Fir && expected == TreeSizeClass.Small);
            Check(row.Role == (understory ? TreePlacementRole.Understory : TreePlacementRole.Canopy), $"a. row {i} role {row.Role}");
            Check(row.Catalog == TreeSourceCatalog.LolipopBase && row.Form == TreeFormKind.Tree && row.EnablingFeature == null && row.SourceBaseVariant == -1, $"a. row {i} base-row fields");
        }
        foreach (TreeSpecies species in new[] { TreeSpecies.Maple, TreeSpecies.Fir, TreeSpecies.Pine })
        {
            var byHeight = _catalog.Where(v => v.Species == species).GroupBy(v => ImportedTreeSourceTable.Row(v.Index).SizeClass)
                .Select(g => (Class: g.Key, Height: g.Average(v => v.HeightMeters), Rank: ImportedTreeSourceTable.Row(g.First().Index).AgeRank)).OrderBy(t => t.Rank).ToList();
            for (int i = 1; i < byHeight.Count; i++) Check(byHeight[i].Height > byHeight[i - 1].Height, $"a. {species} age rank does not follow height ({byHeight[i - 1].Class} then {byHeight[i].Class})");
        }
        var none = new HashSet<string>();
        var feature = new HashSet<string> { FeatureId };
        Check(ImportedTreeSourceTable.RowsEnabled(none).Count == 30 && ImportedTreeSourceTable.RowsEnabled(feature).Count == 55, "a. RowsEnabled: 30 base rows without the feature, 55 with it");
        foreach (int s in SaplingVariants) Check(!ImportedTreeSourceTable.IsCanopySelectable(s, ImportedTreeSourceTable.Row(s).Species, feature), $"a. sapling {s} is canopy selectable");
        Check(ImportedTreeSourceTable.CanopyVariants(TreeSpecies.Fir, TreeSizeClass.Medium).SequenceEqual(new[] { 13 }), "a. fir canopy medium is not {13}");
        Check(ImportedTreeSourceTable.CanopyVariants(TreeSpecies.Fir, TreeSizeClass.Large).SequenceEqual(new[] { 12 }), "a. fir canopy large is not {12}");
        Check(ImportedTreeSourceTable.CanopyVariants(TreeSpecies.Fir, TreeSizeClass.Small).Count == 0 && ImportedTreeSourceTable.CanopyVariants(TreeSpecies.Maple, TreeSizeClass.Sapling).Count == 0, "a. canopy class lists hold a small fir or a sapling");
        Check(!ImportedTreeSourceTable.IsCanopySelectable(6, TreeSpecies.Pine, feature), "a. species mismatch accepted");
        TreeSourceTable test = ImportedTreeSourceTable.CreateForTests(rows.Append(new ImportedTreeSourceRow(99, TreeSpecies.Maple, TreeFormKind.Snag, TreeSizeClass.Medium, 2, TreePlacementRole.DeadWood,
            TreeSourceCatalog.T6Derived, "synthetic", 6, "test.feature")));
        Check(test.RowsEnabled(none).Count == 30 && test.RowsEnabled(new HashSet<string> { "test.feature" }).Count == 31, "a. CreateForTests feature enabling");
        Detail("a. 30 base rows match the catalog (index, species, name), the generation 1 literal ranges and P4's classes");
    }

    private void SamplerChecks()
    {
        Check(ForestStructureSampler.For(1337, Circ, Width, 1f) == ForestStructureSampler.For(1337, Circ, Width, 1f), "For returned two instances for one key");
        Check(ForestStructureSampler.For(1337, Circ, Width, 1f) != ForestStructureSampler.For(1337, Circ, Width, 0.5f), "For ignores the contrast");
        Check(ForestStructureSampler.For(_rootOn).Contrast == 1f, "the review world's default contrast is not 1");
        var flat = ForestStructureSampler.For(1337, Circ, Width, 0f);
        Check(flat.ContrastedStandAge(123456, 54321) == 0.5f, "contrast 0 does not flatten the stand age to 0.5");
        Check(ForestStructureSampler.StemFactor(0) == 1.35f && Math.Abs(ForestStructureSampler.StemFactor(1) - 0.65f) < 1e-6f, "stem factor");
        Check(Math.Abs(ForestStructureSampler.TreelineDensity(0) - 0.15f) < 1e-6f && Math.Abs(ForestStructureSampler.TreelineDensity(0.5f) - 1f) < 1e-6f, "treeline density");
        Check(ForestStructureSampler.SelectSizeClass(TreeSpecies.Maple, 0f, 0.9f) == TreeSizeClass.Medium && ForestStructureSampler.SelectSizeClass(TreeSpecies.Maple, 1f, 0.1f) == TreeSizeClass.Small
            && ForestStructureSampler.SelectSizeClass(TreeSpecies.Pine, 0.5f, 0.95f) == TreeSizeClass.Large && ForestStructureSampler.SelectSizeClass(TreeSpecies.Fir, 0f, 0.99f) == TreeSizeClass.Medium, "class table anchors");
        var regional = new ForestRegionSample(ForestHabitat.DenseForest, 0.6f, 1f, 0f, 0.85f / 1.2f, 0.2f / 1.2f, 0.15f / 1.2f);
        ForestStructureSampler.SpeciesWeights(0.8f, 0.7f, 0.9f, 0f, 0f, 0f, regional, out float m, out float f, out float p);
        Check(Math.Abs(m + f + p - 1f) < 1e-5f && m > f && m > p, $"species weights {m} {f} {p}");
    }

    // ------------------------------------------------------------------ tiles

    private List<Key> Block(string poseName)
    {
        Key c = TileOf(Pose(poseName));
        int lastZ = (int)((Width - 1) / 240);
        var keys = new List<Key>();
        for (long a = c.A - 4; a < c.A + 4; a++) for (long z = c.Z - 4; z < c.Z + 4; z++)
            if (z >= 0 && z <= lastZ) keys.Add(new Key(a, z));
        return keys;
    }

    private void FindTreelineTiles()
    {
        // T 0.22-0.45 on a Forest or Grassland land point of the shared lattice; the tiles of the first 16 such points (design A4).
        var seen = new HashSet<Key>();
        foreach ((int _, int _, double around, double across) in LandscapeWorld.Lattice())
        {
            TerrainSample s = _rootOn.GetTerrainSample((long)Math.Floor(around), (int)Math.Floor(across));
            if (s.Temperature < 0.22f || s.Temperature > 0.45f || s.HasSurfaceWater) continue;
            if (s.Biome != TerrainBiomeId.Forest && s.Biome != TerrainBiomeId.Grassland) continue;
            var key = new Key((long)Math.Floor(Math.Floor(around / AroundCell) / 8.0), (long)Math.Floor(across / 240.0));
            if (seen.Add(key)) _treeline.Add(key);
            if (_treeline.Count == 16) break;
        }
        Check(_treeline.Count == 16, $"found {_treeline.Count} treeline tiles, expected 16");
        // The block is moist, warm lowland where the design's weights give maple almost everywhere, so pine (dry, convex, rocky ground) needs its own tiles:
        // the first 24 lattice points that are Forest or Grassland land with T >= 0.5 and moisture below 0.45 (a test-side addition to design A3's proof).
        var drySeen = new HashSet<Key>();
        foreach ((int _, int _, double around, double across) in LandscapeWorld.Lattice())
        {
            TerrainSample s = _rootOn.GetTerrainSample((long)Math.Floor(around), (int)Math.Floor(across));
            if (s.Temperature < 0.5f || s.Moisture >= 0.45f || s.HasSurfaceWater) continue;
            if (s.Biome != TerrainBiomeId.Forest && s.Biome != TerrainBiomeId.Grassland) continue;
            var key = new Key((long)Math.Floor(Math.Floor(around / AroundCell) / 8.0), (long)Math.Floor(across / 240.0));
            if (drySeen.Add(key)) _dry.Add(key);
            if (_dry.Count == 24) break;
        }
        Check(_dry.Count >= 8, $"found only {_dry.Count} dry tiles");
        // Relief and slope move pine only on hilly ground, so the habitat set also holds the 3 x 3 tiles around the steep and hilly poses of the dressing and T0 fixtures.
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json"));
        foreach (LandscapePose p in dressing.Poses.Where(p => p.Name is "steep_eye" or "scree_eye" or "cliff_eye" or "breaks_300m").Concat(_fixture.Poses.Where(p => p.Name is "hills_eye" or "hills_300m")))
        {
            Key c = TileOf(p);
            for (long a = c.A - 1; a <= c.A + 1; a++) for (long z = c.Z - 1; z <= c.Z + 1; z++) if (z >= 0 && drySeen.Add(new Key(a, z))) _dry.Add(new Key(a, z));
        }
    }

    private void BuildTiles()
    {
        _block.AddRange(Block("forest_interior_eye"));
        FindTreelineTiles();
        var onKeys = new HashSet<Key>(_block);
        foreach (Key k in _treeline.Concat(_dry)) onKeys.Add(k);
        var poseTiles = new List<Key>();
        foreach (string name in new[] { "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye" })
        {
            Key c = TileOf(Pose(name));
            for (long a = c.A - 1; a <= c.A + 1; a++) for (long z = c.Z - 1; z <= c.Z + 1; z++) if (z >= 0) { poseTiles.Add(new Key(a, z)); onKeys.Add(new Key(a, z)); }
        }
        BuildMany(_rootOn, onKeys.ToList(), _on);
        BuildMany(_rootOff, poseTiles.Distinct().ToList(), _off);
        Detail($"tiles: block {_block.Count}, treeline {_treeline.Count}, pose tiles {poseTiles.Distinct().Count()}; on-world builds {_on.Count}, off-world builds {_off.Count}");
    }

    private void BuildMany(RingTerrainSampler root, List<Key> keys, Dictionary<Key, List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>> into)
    {
        var samplers = new RingTerrainSampler[_workers];
        for (int i = 0; i < _workers; i++) samplers[i] = root.CreateIndependentCopy(); // on the main thread
        var results = new ConcurrentDictionary<Key, List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>>();
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, k =>
        {
            for (int i = k; i < keys.Count; i += _workers)
                results[keys[i]] = RingBiomeDecorationLayer.BuildImportedForestReviewTile(samplers[k], keys[i].A, keys[i].Z, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width);
        });
        foreach (RingTerrainSampler s in samplers) s.Dispose();
        foreach (var pair in results) into[pair.Key] = pair.Value;
    }

    // ------------------------------------------------------------------ h. determinism

    private void Determinism()
    {
        Key key = _block[_block.Count / 2];
        var a = RingBiomeDecorationLayer.BuildImportedForestReviewTile(_rootOn, key.A, key.Z, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width);
        var b = RingBiomeDecorationLayer.BuildImportedForestReviewTile(_rootOn, key.A, key.Z, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width);
        using RingTerrainSampler copy = _specOn.CreateSampler();
        var c = RingBiomeDecorationLayer.BuildImportedForestReviewTile(copy, key.A, key.Z, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width);
        Check(a.Count > 0, "h. the determinism tile has no trees");
        Check(a.SequenceEqual(b), "h. the same tile built twice differs");
        Check(a.SequenceEqual(c), "h. the tile built from a second sampler differs");
        Check(a.SequenceEqual(_on[key]), "h. the tile built on a worker thread differs from the main thread build");
        Detail($"h. tile ({key.A},{key.Z}): {a.Count} records, identical across two builds, a second sampler and a worker thread");
    }

    // ------------------------------------------------------------------ b. the structured path reads the table

    private void SourceChecks()
    {
        var feature = new HashSet<string> { FeatureId };
        long total = 0, bad = 0;
        var perClass = new Dictionary<string, int>();
        foreach (var pair in _on) foreach (var r in pair.Value)
        {
            total++;
            bool sapling = Array.IndexOf(SaplingVariants, r.Variant) >= 0;
            bool selectable = ImportedTreeSourceTable.IsCanopySelectable(r.Variant, r.Species, feature);
            if (sapling || !selectable) { bad++; if (bad <= 3) Check(false, $"b. record variant {r.Variant} ({r.Species}) is {(sapling ? "a sapling" : "not canopy selectable")}"); }
            else
            {
                var row = ImportedTreeSourceTable.Row(r.Variant);
                bool inClass = ImportedTreeSourceTable.CanopyVariants(row.Species, row.SizeClass).Contains(r.Variant);
                if (!inClass) Check(false, $"b. variant {r.Variant} is not listed in its class");
                string k = row.Species + "-" + row.SizeClass; perClass[k] = perClass.GetValueOrDefault(k) + 1;
            }
        }
        Check(total > 0, "b. no feature-on records");
        Check(bad == 0, $"b. {bad} of {total} feature-on records hold a sapling or a non-canopy variant");
        Detail($"b. {total} feature-on records, {bad} saplings or non-canopy; class mix: " + string.Join(", ", perClass.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")));
    }

    // ------------------------------------------------------------------ analysis

    private List<Rec> Analyse()
    {
        var dryOnly = new HashSet<Key>(_dry);
        dryOnly.ExceptWith(_block.Concat(_treeline));
        var keys = _block.Concat(_treeline).Concat(_dry).Distinct().ToList();
        var results = new ConcurrentBag<Rec>();
        var contexts = new Ctx[_workers];
        for (int i = 0; i < _workers; i++)
        {
            RingTerrainSampler s = _rootOn.CreateIndependentCopy();
            s.EnablePresentationLatticeCache();
            contexts[i] = new Ctx { S = s, F = new ForestRegionSampler(LandscapeWorld.Seed, Circ, Width), Structure = ForestStructureSampler.For(_rootOn) };
        }
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, k =>
        {
            Ctx c = contexts[k];
            for (int i = k; i < keys.Count; i += _workers)
            {
                c.G = new RingHabitatFieldGrid(c.S, EmptyEdits(c.S), LandscapeWorld.Seed, Circ, Width); // a fresh memo per tile keeps it small
                foreach (var r in _on[keys[i]])
                {
                    TerrainSample t = c.S.GetTerrainSample(r.Around, r.Across);
                    float riseX = c.S.GetPresentationSurfaceHeight(r.Around + 2, r.Across) - c.S.GetPresentationSurfaceHeight(r.Around - 2, r.Across);
                    float riseZ = c.S.GetPresentationSurfaceHeight(r.Around, r.Across + 2) - c.S.GetPresentationSurfaceHeight(r.Around, r.Across - 2);
                    float slope = Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(riseX * riseX + riseZ * riseZ) / 4.0f));
                    ForestRegionSample regional = c.F.Sample(r.Around, r.Across, t, slope);
                    float age = c.Structure.EffectiveAge(r.Around, r.Across, regional, t.Temperature);
                    RingHabitatSample h = c.G.Sample(r.Around, r.Across);
                    ForestStructureSampler.SpeciesWeights(t.Moisture, t.Temperature, h.WetnessTotal, h.Relief64, h.RockExposure, slope, regional, out _, out _, out float pinePred);
                    var row = ImportedTreeSourceTable.Row(r.Variant);
                    int maxRank = _maxRank[row.Species];
                    results.Add(new Rec(r.Species, r.Variant, r.Height, t.Temperature, t.Moisture, h.WetnessTotal, h.Relief64, h.RockExposure, age, row.AgeRank / (double)maxRank, r.Around, r.Across, dryOnly.Contains(keys[i]), pinePred, slope));
                }
            }
        });
        foreach (Ctx c in contexts) c.S.Dispose();
        return results.ToList();
    }

    private static string Bar(double[] values) => string.Join(" ", values.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ c. size class follows stand age

    private void AgeCheck(List<Rec> recs)
    {
        var sorted = recs.OrderBy(r => r.Age).ToList();
        int n = sorted.Count;
        Check(n >= 500, $"c. only {n} records");
        var means = new double[5]; var errors = new double[5];
        for (int q = 0; q < 5; q++)
        {
            var bin = sorted.Skip(q * n / 5).Take((q + 1) * n / 5 - q * n / 5).ToList();
            means[q] = bin.Average(r => r.Rank);
            double variance = bin.Sum(r => (r.Rank - means[q]) * (r.Rank - means[q])) / Math.Max(1, bin.Count - 1);
            errors[q] = Math.Sqrt(variance / Math.Max(1, bin.Count));
        }
        for (int q = 1; q < 5; q++) Check(means[q] >= means[q - 1], $"c. mean age rank falls from quintile {q} ({means[q - 1]:F3}) to {q + 1} ({means[q]:F3})");
        Check(means[4] > means[0] + 0.05, $"c. the age rank barely rises ({means[0]:F3} to {means[4]:F3})");
        Detail($"c. {n} records; mean normalised age rank by effective-age quintile: {Bar(means)} (SE {Bar(errors)}); age at quintile edges: " + string.Join(" ", Enumerable.Range(0, 5).Select(q => sorted[q * n / 5].Age.ToString("F2", CultureInfo.InvariantCulture))));
    }

    // ------------------------------------------------------------------ d. species follow habitat

    /// <summary>The share of <paramref name="hit"/> by tercile of <paramref name="x"/> inside each equal-count stratum of <paramref name="control"/>, averaged over the strata; non-decreasing within one standard error, rising overall.</summary>
    private void Stratified(string label, List<Rec> recs, Func<Rec, double> control, Func<Rec, double> x, Func<Rec, bool> hit, int strata = 6, int bins = 3)
    {
        var sorted = recs.OrderBy(control).ToList();
        int n = sorted.Count;
        var share = new double[bins]; var variance = new double[bins]; var predicted = new double[bins]; int used = 0;
        for (int s = 0; s < strata; s++)
        {
            var stratum = sorted.Skip(s * n / strata).Take((s + 1) * n / strata - s * n / strata).OrderBy(x).ToList();
            int m = stratum.Count;
            if (m < 30 * bins) continue;
            used++;
            for (int b = 0; b < bins; b++)
            {
                var bin = stratum.Skip(b * m / bins).Take((b + 1) * m / bins - b * m / bins).ToList();
                double pc = (bin.Count(hit) + 1.0) / (bin.Count + 2.0);
                share[b] += bin.Count(hit) / (double)bin.Count;
                predicted[b] += bin.Average(r => (double)r.PinePred);
                variance[b] += pc * (1 - pc) / (bin.Count + 2.0);
            }
        }
        if (used < 3) { Check(false, $"d. {label}: only {used} strata with enough records"); return; }
        for (int b = 0; b < bins; b++) { share[b] /= used; variance[b] = variance[b] / used / used; }
        Detail($"d. {label} (stratified, {used} strata): model pine weight by tercile " + Bar(predicted.Select(v => v / used).ToArray()) + ", shares by tercile " + Bar(share) + " (SE " + Bar(variance.Select(Math.Sqrt).ToArray()) + ")");
        for (int b = 1; b < bins; b++)
            Check(share[b] >= share[b - 1] - Math.Max(0.005, Math.Max(Math.Sqrt(variance[b]), Math.Sqrt(variance[b - 1]))), $"d. {label}: stratified share falls from tercile {b} ({share[b - 1]:F3}) to {b + 1} ({share[b]:F3}) by more than one standard error");
        Check(share[^1] > share[0], $"d. {label}: the stratified share does not rise ({share[0]:F3} to {share[^1]:F3})");
    }

    private void Monotone(string label, List<Rec> recs, Func<Rec, double> x, Func<Rec, bool> hit, int bins = 5)
    {
        var sorted = recs.OrderBy(x).ToList();
        int n = sorted.Count;
        var shares = new List<double>(); var errors = new List<double>(); var counts = new List<int>();
        for (int b = 0; b < bins; b++)
        {
            var bin = sorted.Skip(b * n / bins).Take((b + 1) * n / bins - b * n / bins).ToList();
            if (bin.Count < 30) continue;
            double p = bin.Count(hit) / (double)bin.Count;
            // Binomial SE of the Agresti-Coull share (so a saturated 0 or 1 still has a nonzero error), floored at half a percent: a saturated bin may wobble by a tree or two.
            double pc = (bin.Count(hit) + 1.0) / (bin.Count + 2.0);
            shares.Add(p); errors.Add(Math.Max(0.005, Math.Sqrt(pc * (1 - pc) / (bin.Count + 2.0)))); counts.Add(bin.Count);
        }
        string text = $"d. {label}: shares by bin: " + Bar(shares.ToArray()) + " (n " + string.Join("/", counts) + ")";
        if (shares.Count < 3) { Check(false, $"d. {label}: only {shares.Count} bins with 30 records ({text})"); Detail(text); return; }
        for (int i = 1; i < shares.Count; i++)
            Check(shares[i] >= shares[i - 1] - Math.Max(errors[i], errors[i - 1]), $"d. {label}: share falls from bin {i} ({shares[i - 1]:F3}) to {i + 1} ({shares[i]:F3}) by more than one standard error");
        Check(shares[^1] > shares[0], $"d. {label}: the share does not rise ({shares[0]:F3} to {shares[^1]:F3})");
        Detail(text);
    }

    private void HabitatCheck(List<Rec> recs)
    {
        Detail($"d. species over {recs.Count} records: maple {recs.Count(r => r.Species == TreeSpecies.Maple)}, fir {recs.Count(r => r.Species == TreeSpecies.Fir)}, pine {recs.Count(r => r.Species == TreeSpecies.Pine)}");
        // Maple and pine against habitat on warm records only (T >= 0.5), so the treeline term does not confound them; fir against cold on every record of the block and treeline tiles.
        List<Rec> warm = recs.Where(r => r.T >= 0.5f).ToList();
        Detail($"d. warm records {warm.Count}: maple {warm.Count(r => r.Species == TreeSpecies.Maple)}, fir {warm.Count(r => r.Species == TreeSpecies.Fir)}, pine {warm.Count(r => r.Species == TreeSpecies.Pine)}");
        Detail($"d. warm records with slope >= 10 deg: {warm.Count(r => r.Slope >= 10f)}, >= 6 deg: {warm.Count(r => r.Slope >= 6f)}");
        var r64 = warm.Select(r => (double)r.R64).OrderBy(v => v).ToList();
        Detail($"d. warm R64 deciles: " + string.Join(" ", Enumerable.Range(0, 11).Select(i => r64[Math.Min(r64.Count - 1, i * r64.Count / 10)].ToString("F3", CultureInfo.InvariantCulture))));
        Monotone("maple share vs wetness", warm, r => r.W, r => r.Species == TreeSpecies.Maple);
        Monotone("pine share vs dryness", warm, r => 1.0 - r.M, r => r.Species == TreeSpecies.Pine);
        // Relief only moves pine where moisture does not already decide the species, so hold moisture fixed: on slopes of 10 degrees or more (where the relief term is active), the share by relief tercile inside each of three moisture strata (moisture 0.30-0.65), averaged over the strata.
        Stratified("pine share vs relief 64 m (slope >= 10 deg)", warm.Where(r => r.M >= 0.30f && r.M < 0.65f && r.Slope >= 10f).ToList(), r => r.M, r => r.R64, r => r.Species == TreeSpecies.Pine, 3);
        Monotone("fir share vs cold", recs.Where(r => !r.Dry).ToList(), r => 1.0 - r.T, r => r.Species == TreeSpecies.Fir);
    }

    // ------------------------------------------------------------------ e. treeline

    private void TreelineCheck(List<Rec> recs)
    {
        // Area per T bin: a 24 m grid over every tile of the set, counting vegetable points (Forest or Grassland, no surface water, no site block).
        const double Step = 0.03, Top = 0.55;
        var area = new ConcurrentDictionary<int, double>();
        var keys = _block.Concat(_treeline).Distinct().ToList();
        var samplers = new RingTerrainSampler[_workers];
        for (int i = 0; i < _workers; i++) samplers[i] = _rootOn.CreateIndependentCopy();
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, k =>
        {
            for (int i = k; i < keys.Count; i += _workers)
                for (int gi = 0; gi < 10; gi++) for (int gj = 0; gj < 10; gj++)
                {
                    long around = (long)((keys[i].A * 8 * AroundCell) + (gi + 0.5) * (8 * AroundCell / 10.0));
                    int across = (int)(keys[i].Z * 240 + (gj + 0.5) * 24.0);
                    TerrainSample s = samplers[k].GetTerrainSample(around, across);
                    if (s.HasSurfaceWater || s.SiteMask.BlocksVegetation || (s.Biome != TerrainBiomeId.Forest && s.Biome != TerrainBiomeId.Grassland)) continue;
                    area.AddOrUpdate((int)Math.Floor(s.Temperature / Step), 8 * AroundCell / 10.0 * 24.0, (_, v) => v + 8 * AroundCell / 10.0 * 24.0);
                }
        });
        foreach (RingTerrainSampler s in samplers) s.Dispose();
        var rows = new List<(int Bin, double Ha, int Trees, double Stems, double StemsError, double Height, double HeightError)>();
        foreach (var group in recs.Where(r => !r.Dry).GroupBy(r => (int)Math.Floor(r.T / Step)).OrderBy(g => g.Key))
        {
            if (!area.TryGetValue(group.Key, out double m2) || m2 < 100000.0 || group.Count() < 20 || (group.Key + 1) * Step > Top) continue;
            double ha = m2 / 10000.0;
            double mean = group.Average(r => r.Height);
            double sd = Math.Sqrt(group.Sum(r => (r.Height - mean) * (r.Height - mean)) / Math.Max(1, group.Count() - 1));
            rows.Add((group.Key, ha, group.Count(), group.Count() / ha, Math.Sqrt(group.Count()) / ha, mean, sd / Math.Sqrt(group.Count())));
        }
        Check(rows.Count >= 3, $"e. only {rows.Count} temperature bins with enough area and trees");
        for (int i = 1; i < rows.Count; i++)
        {
            Check(rows[i].Stems >= rows[i - 1].Stems - Math.Max(rows[i].StemsError, rows[i - 1].StemsError), $"e. stems per hectare fall from T bin {rows[i - 1].Bin * Step:F2} ({rows[i - 1].Stems:F1}) to {rows[i].Bin * Step:F2} ({rows[i].Stems:F1})");
            Check(rows[i].Height >= rows[i - 1].Height - Math.Max(rows[i].HeightError, rows[i - 1].HeightError), $"e. mean height falls from T bin {rows[i - 1].Bin * Step:F2} ({rows[i - 1].Height:F2}) to {rows[i].Bin * Step:F2} ({rows[i].Height:F2})");
        }
        if (rows.Count >= 2)
        {
            Check(rows[^1].Stems > rows[0].Stems, "e. stems per hectare do not rise from the coldest to the warmest bin");
            Check(rows[^1].Height > rows[0].Height, "e. mean height does not rise from the coldest to the warmest bin");
        }
        Detail("e. treeline (T bin: ha, trees, stems/ha +-SE, mean height m +-SE): " + string.Join("; ", rows.Select(r => $"{r.Bin * Step:F2}: {r.Ha:F1} ha, {r.Trees}, {r.Stems:F1}+-{r.StemsError:F1}, {r.Height:F2}+-{r.HeightError:F2}")));
    }

    // ------------------------------------------------------------------ f. feature off is the current build

    private sealed class Fnv
    {
        private ulong _h = 0xCBF29CE484222325UL;
        private void Byte(byte b) { _h ^= b; _h *= 0x100000001B3UL; }
        public void U64(ulong v) { for (int i = 0; i < 8; i++) Byte((byte)(v >> (8 * i))); }
        public void U32(uint v) { for (int i = 0; i < 4; i++) Byte((byte)(v >> (8 * i))); }
        public void I32(int v) => U32((uint)v);
        public void I64(long v) => U64((ulong)v);
        public void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));
        public string Hex => "0x" + _h.ToString("X16", CultureInfo.InvariantCulture);
    }

    private void FeatureOff()
    {
        // The eight fingerprint tiles, as WorldGenerationFingerprint.Compute builds them (cell 30), against the committed generation 1 golden.
        string path = ProjectSettings.GlobalizePath(WorldGenerationFingerprintTest.GoldenPath);
        JsonElement row = WorldGenerationFingerprintTest.ReadRows(path)[1];
        var golden = row.GetProperty("trees").EnumerateArray().ToDictionary(e => e.GetProperty("pose").GetString()!, e => (Hash: e.GetProperty("hash").GetString()!, Count: e.GetProperty("count").GetInt32()));
        int matched = 0;
        foreach (string name in WorldGenerationFingerprint.TilePoses)
        {
            LandscapePose p = Pose(name);
            long tx = (long)Math.Floor(p.Around / 30.0) / 8, tz = (long)Math.Floor(p.Across / 30.0) / 8;
            var records = RingBiomeDecorationLayer.BuildImportedForestReviewTile(_rootOff, tx, tz, Cells, 30d, LandscapeWorld.Seed, Circ, Width);
            var h = new Fnv();
            foreach (var r in records) { h.U64(r.Id); h.I64(r.Around); h.I32(r.Across); h.F32(r.SurfaceHeight); h.F32(r.Width); h.F32(r.Height); h.F32(r.Yaw); h.I32(r.Variant); h.I32((int)r.Species); }
            bool same = golden[name].Hash == h.Hex && golden[name].Count == records.Count;
            Check(same, $"f. {name}: generation 1 tree hash {h.Hex} ({records.Count}) differs from the golden {golden[name].Hash} ({golden[name].Count})");
            if (same) matched++;
        }
        Detail($"f. feature off: {matched} of 8 fingerprint tile hashes equal the generation 1 golden");
        // The review world's tiles differ from generation 1 (the feature does something), and feature off equals LandscapeWorld.Default on the pose tiles.
        int differing = 0, identical = 0;
        foreach (var pair in _off)
            if (_on.TryGetValue(pair.Key, out var on)) { if (on.SequenceEqual(pair.Value)) identical++; else differing++; }
        Check(differing > 0, "f. the feature changed no pose tile");
        using RingTerrainSampler legacy = LandscapeWorld.Default.CreateSampler();
        Key interior = TileOf(Pose("forest_interior_eye"));
        var offLegacy = RingBiomeDecorationLayer.BuildImportedForestReviewTile(legacy, interior.A, interior.Z, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width);
        Check(offLegacy.SequenceEqual(_off[interior]), "f. LandscapeWorld.Default and For(1) differ on the interior tile");
        Detail($"f. pose tiles: {differing} differ between the review world and generation 1, {identical} identical (no forest)");
    }

    // ------------------------------------------------------------------ g. per-pose triangle sums

    private void Poses()
    {
        foreach (string name in new[] { "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye" })
        {
            LandscapePose p = Pose(name);
            Key c = TileOf(p);
            (long Lod2, long Lod0, int Records) Sum(Dictionary<Key, List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>> world)
            {
                long lod2 = 0, lod0 = 0; int records = 0;
                for (long a = c.A - 1; a <= c.A + 1; a++) for (long z = c.Z - 1; z <= c.Z + 1; z++)
                {
                    if (!world.TryGetValue(new Key(a, z), out var list)) continue;
                    foreach (var r in list)
                    {
                        double da = r.Around - p.Around; if (da > Circ * 0.5) da -= Circ; else if (da < -Circ * 0.5) da += Circ;
                        double d = Math.Sqrt(da * da + (r.Across - p.Across) * (r.Across - p.Across));
                        if (d > 300.0) continue;
                        records++;
                        if (d < 40.0) lod0 += _triangles[r.Variant, 0]; else lod2 += _triangles[r.Variant, 2];
                    }
                }
                return (lod2, lod0, records);
            }
            var on = Sum(_on); var off = Sum(_off);
            double lod2Ratio = off.Lod2 > 0 ? on.Lod2 / (double)off.Lod2 : double.NaN, lod0Ratio = off.Lod0 > 0 ? on.Lod0 / (double)off.Lod0 : double.NaN;
            Detail($"g. {name}: records within 300 m on {on.Records} off {off.Records}; LOD2 (40-300 m) triangles on {on.Lod2} off {off.Lod2} ratio {lod2Ratio:F3}; LOD0 (<40 m) on {on.Lod0} off {off.Lod0} ratio {lod0Ratio:F3}");
            Check(off.Lod2 > 0, $"g. {name}: no generation 1 LOD2 triangles to compare");
            Check(!(lod2Ratio > 1.10), $"g. {name}: LOD2 triangle ratio {lod2Ratio:F3} exceeds 1.10 (escalation condition)");
        }
    }
}
