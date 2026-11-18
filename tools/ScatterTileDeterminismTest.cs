using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using Ctx = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterBuildContext;
using Key = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTileKey;
using TreeSet = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTreeSet;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T5-4a (docs/work-orders/terrain-vegetation/T5.md, design A5-A9): the scatter framework. Class table (validation, hash, build order), the pure tile builder (identity across runs, build orders and
/// worker counts, seams, placement invariants), the tile file (round trip, corruption, versioned path), the disk-cache gate, the feature-off path, trees untouched (A7), the halo tree mirror, and cost
/// (printed). Production has no rows yet, so the builder runs on a three-row test table. Prints SCATTER_TILE_DETERMINISM=PASS. Headless: tools/run_tests.ps1 -Tests scatter_tile_determinism_test.
/// </summary>
public partial class ScatterTileDeterminismTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const string DressingFixture = "res://tools/fixtures/landscape_poses_dressing_s1337.json";
    private static readonly string[] DressingNames = { "steep_eye", "scree_eye", "cliff_eye", "breaks_300m", "mountain_2km", "lowland_eye" };
    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("SCATTER_TILE_DETERMINISM_DETAIL " + text);

    private readonly record struct Tile(string Name, long TA, long TZ);

    private LandscapeWorld.Spec _spec = null!;
    private RingTerrainSampler _root = null!;
    private RingScatterClassTable _table = null!;
    private Ctx _ctx = null!;
    private readonly List<Tile> _tiles = new();
    private readonly Dictionary<string, TreeSet> _treeSets = new();
    private double _aroundCell;

    public int Run()
    {
        string cacheRoot = ProjectSettings.GlobalizePath("res://.godot/scatter_tile_determinism_cache");
        try
        {
            Setup();
            TableChecks();
            Identity();
            Seams();
            FileChecks(cacheRoot);
            DiskGate();
            FeatureOff();
            TreesUntouched();
            HaloMirror();
            Ordering();
            StreamerChecks(cacheRoot + "_streamer");
            Cost();
        }
        catch (Exception e) { Console.Error.WriteLine("SCATTER_TILE_DETERMINISM=FAIL exception " + e); return 1; }
        finally
        {
            RingScatterTileFile.ScatterDiskCacheForTests = false;
            try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true); if (Directory.Exists(cacheRoot + "_streamer")) Directory.Delete(cacheRoot + "_streamer", true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _root?.Dispose();
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("SCATTER_TILE_DETERMINISM=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("SCATTER_TILE_DETERMINISM=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixtures

    private static RingScatterResponse R(RingHabitatField f, float in0, float in1, float out0 = 0f, float out1 = 1f) => new(f, in0, in1, out0, out1);

    /// <summary>Three rows that exercise every mechanism: a long-reach 64 m row, an 8 m row that avoids it and has tree clearance and a nearest-tree multiplier, and a 60 m block row that avoids both.</summary>
    private static RingScatterClassTable BuildTestTable(int mid = 2, int longId = 1, int block = 3) => RingScatterClassTable.CreateForTests(new[]
    {
        new RingScatterClass
        {
            Id = (ushort)longId, Key = "test.long", RenderGroup = RingScatterRenderGroup.Outcrops, ReachM = 2500f, CellM = 64f, CandidatesPerCell = 1, BaseDensityPerHa = 400f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.RockExposure, 0.05f, 0.6f) }),
                new RingScatterTerm(0.5f, new[] { R(RingHabitatField.Slope, 18f, 30f), R(RingHabitatField.Relief64, 0.35f, 0.8f) }),
                new RingScatterTerm(0.002f, Array.Empty<RingScatterResponse>()),
            },
            SpacingM = 30f, SpacingSizeFactor = 0.6f, TreeClearanceM = 2f, TreeClearanceSizeFactor = 0.5f, SizeMinM = 5f, SizeMaxM = 40f, SizeExponent = 2.2f,
            MeshFirst = 0, MeshCount = 4, Alignment = RingScatterAlignment.BeddingHorizontal, BlockM = 0
        },
        new RingScatterClass
        {
            Id = (ushort)mid, Key = "test.mid", RenderGroup = RingScatterRenderGroup.Boulders, ReachM = 600f, CellM = 8f, CandidatesPerCell = 2, BaseDensityPerHa = 60f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Scree, 0f, 1f) }),
                new RingScatterTerm(0.4f, new[] { R(RingHabitatField.Slope, 4f, 15f) }),
                new RingScatterTerm(0.02f, Array.Empty<RingScatterResponse>()),
            },
            Multipliers = new[] { R(RingHabitatField.Canopy, 0f, 1f, 1f, 0.7f), R(RingHabitatField.NearestTreeM, 0f, 6f, 0.4f, 1f) },
            SpacingM = 0.3f, SpacingSizeFactor = 0.9f, Avoid = new[] { new RingScatterAvoid((ushort)longId, 2f, 0.5f) }, TreeClearanceM = 1f, TreeClearanceSizeFactor = 0.5f,
            SizeMinM = 1f, SizeMaxM = 4f, SizeExponent = 1.6f, MeshFirst = 4, MeshCount = 6, Alignment = RingScatterAlignment.SlopeAligned, AlignFactor = 0.6f, RandomTiltDeg = 8f, BlockM = 0
        },
        new RingScatterClass
        {
            Id = (ushort)block, Key = "test.block", RenderGroup = RingScatterRenderGroup.Cobbles, ReachM = 50f, CellM = 8f, CandidatesPerCell = 4, BaseDensityPerHa = 200f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Scree, 0f, 1f) }),
                new RingScatterTerm(0.3f, new[] { R(RingHabitatField.RockExposure, 0f, 1f) }),
            },
            Multipliers = new[] { R(RingHabitatField.Canopy, 0f, 1f, 1f, 0.5f) },
            SpacingM = 0.1f, SpacingSizeFactor = 1.0f, Avoid = new[] { new RingScatterAvoid((ushort)longId, 2f, 0.5f), new RingScatterAvoid((ushort)mid, 0.3f, 0.5f) },
            TreeClearanceM = 0.6f, TreeClearanceSizeFactor = 0.5f, SizeMinM = 0.8f, SizeMaxM = 1.6f, SizeExponent = 1f, MeshFirst = 10, MeshCount = 3,
            Alignment = RingScatterAlignment.SlopeAligned, AlignFactor = 1f, RandomTiltDeg = 4f, BlockM = 60
        },
    });

    private void Setup()
    {
        _spec = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" });
        _root = _spec.CreateSampler();
        Check((_root.WorldFeatureMask & (1 << 4)) != 0, $"the review world's feature mask {_root.WorldFeatureMask} lacks bit 4 (scatter.rocks)");
        Detail($"world: LandscapeWorld.For(1, [ground.surface, scatter.rocks]) tag={_spec.Tag} mask={_root.WorldFeatureMask}");
        _aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        void Add(LandscapePose p) => _tiles.Add(new Tile(p.Name, (long)Math.Floor(Math.Floor(p.Around / _aroundCell) / 8.0), (long)Math.Floor(p.Across / 240.0)));
        foreach (string name in WorldGenerationFingerprint.TilePoses) Add(t0.Poses.First(p => p.Name == name));
        foreach (string name in DressingNames) Add(dressing.Poses.First(p => p.Name == name));
        Check(_tiles.Count == 14, "expected 14 test tiles, got " + _tiles.Count);
        _table = BuildTestTable();
        Check(_table.Validate().Count == 0, "the test table does not validate: " + string.Join("; ", _table.Validate()));
        _ctx = Ctx.ForReview(LandscapeWorld.Seed, Circ, Width, _table, null, _root.WorldFeatureMask);
    }

    private Tile T(string name) => _tiles.First(t => t.Name == name);
    private static Key KeyOf(Tile t) => new(t.TA, t.TZ);
    private static Key KeyOf(long a, long z) => new(a, z);

    private TreeSet Trees(Key key)
    {
        string id = key.Around + "_" + key.Across;
        if (!_treeSets.TryGetValue(id, out TreeSet? set)) _treeSets[id] = set = _ctx.BuildTreeSet(_root, key);
        return set;
    }

    private RingBiomeDecorationLayer.ScatterRowsResult Build(Key key, TreeSet trees, IReadOnlyList<ScatterRecord> earlier, int from, int to, int mask, RingTerrainSampler? sampler = null)
    {
        if (sampler != null) return RingBiomeDecorationLayer.BuildScatterRows(_ctx, sampler, key, trees, earlier, from, to, mask);
        using RingTerrainSampler copy = _root.CreateIndependentCopy();
        return RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, key, trees, earlier, from, to, mask);
    }

    private List<ScatterRecord> All(Key key) => Build(key, Trees(key), Array.Empty<ScatterRecord>(), 0, _table.BuildOrder.Count, 0xFFFF).Records;

    private static bool Same(IReadOnlyList<ScatterRecord> a, IReadOnlyList<ScatterRecord> b) => a.Count == b.Count && a.SequenceEqual(b);

    // ------------------------------------------------------------------ a. table

    private void TableChecks()
    {
        Check(_table.Validate().Count == 0, "a. valid table failed Validate");
        // A row that Avoids a later row.
        var bad = RingScatterClassTable.CreateForTests(new[]
        {
            new RingScatterClass { Id = 1, ReachM = 100f, Avoid = new[] { new RingScatterAvoid(2, 1f, 0f) } },
            new RingScatterClass { Id = 2, ReachM = 50f },
        });
        Check(bad.Validate().Count > 0, "a. a row that avoids a later row passed Validate");
        var self = RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 1, ReachM = 100f, Avoid = new[] { new RingScatterAvoid(1, 1f, 0f) } } });
        Check(self.Validate().Count > 0, "a. a row that avoids itself passed Validate");
        var dup = RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 1 }, new RingScatterClass { Id = 1, ReachM = 5f } });
        Check(dup.Validate().Count > 0, "a. duplicate ids passed Validate");
        Check(RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 0 } }).Validate().Count > 0, "a. id 0 passed Validate");
        Check(RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 1, CellM = 24f } }).Validate().Count > 0, "a. CellM 24 passed Validate");
        Check(RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 1, BlockM = 30 } }).Validate().Count > 0, "a. BlockM 30 passed Validate");
        Check(RingScatterClassTable.CreateForTests(new[] { new RingScatterClass { Id = 1, Alignment = (RingScatterAlignment)9 } }).Validate().Count > 0, "a. an unknown alignment passed Validate");
        Check(RingScatterClassTable.Production.Validate().Count == 0 && RingScatterClassTable.Production.Rows.Count == 0, "a. Production is not an empty valid table");

        // Hash changes when any single column changes (spot-check 5 columns, plus a term and the table size).
        RingScatterClass baseRow = _table.Rows[1];
        ulong baseHash = _table.Hash;
        var variants = new Dictionary<string, RingScatterClass>
        {
            ["BaseDensityPerHa"] = baseRow with { BaseDensityPerHa = baseRow.BaseDensityPerHa + 1f },
            ["SpacingSizeFactor"] = baseRow with { SpacingSizeFactor = baseRow.SpacingSizeFactor + 0.01f },
            ["TreeClearanceM"] = baseRow with { TreeClearanceM = baseRow.TreeClearanceM + 0.1f },
            ["Alignment"] = baseRow with { Alignment = RingScatterAlignment.LyingAlongSlope },
            ["BlockM"] = baseRow with { BlockM = 60 },
            ["Key"] = baseRow with { Key = baseRow.Key + "x" },
            ["Terms"] = baseRow with { Terms = new[] { new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Scree, 0f, 0.9f) }) } },
            ["Multipliers"] = baseRow with { Multipliers = Array.Empty<RingScatterResponse>() },
            ["Avoid"] = baseRow with { Avoid = new[] { new RingScatterAvoid(1, 2.5f, 0.5f) } },
        };
        foreach ((string column, RingScatterClass variant) in variants)
        {
            ulong h = RingScatterClassTable.CreateForTests(_table.Rows.Select(r => r.Id == baseRow.Id ? variant : r)).Hash;
            Check(h != baseHash, $"a. changing {column} did not change the table hash");
        }
        Check(RingScatterClassTable.CreateForTests(_table.Rows.Take(2)).Hash != baseHash, "a. removing a row did not change the table hash");
        Check(RingScatterClassTable.CreateForTests(_table.Rows.Reverse()).Hash == baseHash, "a. the hash depends on the order the rows are listed in, not only on ids");
        Detail($"a. table: hash {baseHash:X16}, {variants.Count} single-column changes all change it; build order {string.Join(",", _table.BuildOrder.Select(r => r.Id))}");

        // UpOct round trip and tint pack.
        double worst = 0;
        var rng = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1;
            double l = Math.Sqrt(x * x + y * y + z * z); x /= l; y /= l; z /= l;
            var d = ScatterRecord.DecodeUpOct(ScatterRecord.EncodeUpOct(x, y, z));
            worst = Math.Max(worst, Math.Acos(Math.Clamp(x * d.X + y * d.Y + z * d.Z, -1.0, 1.0)));
        }
        Check(worst < 0.002, $"a. UpOct round trip error {worst} rad");
        var up = ScatterRecord.DecodeUpOct(ScatterRecord.EncodeUpOct(0, 1, 0));
        Check(Math.Abs(up.Y - 1.0) < 1e-6, "a. up does not round trip");
        Check((ScatterRecord.PackBaseTint(1f, 0.5f, 0f) >> 24) == 0 && ScatterRecord.UnpackBaseTint(ScatterRecord.PackBaseTint(1f, 0.5f, 0f)).R == 1f, "a. tint pack");
    }

    // ------------------------------------------------------------------ b. identity

    private static readonly string[] FocusTiles = { "steep_eye", "scree_eye", "forest_edge_eye", "plains_eye" };

    private int OrderIndex(ushort classId) => _table.BuildIndexOf(classId);

    private void Identity()
    {
        var reference = new Dictionary<string, List<ScatterRecord>>();
        foreach (string name in FocusTiles)
        {
            Tile tile = T(name);
            Key key = KeyOf(tile);
            TreeSet trees = Trees(key);
            List<ScatterRecord> a = All(key), b = All(key);
            reference[name] = a;
            Check(Same(a, b), $"b. {name}: two runs differ");
            Check(a.Zip(a.Skip(1)).All(p => (OrderIndex(p.First.ClassId), p.First.Id).CompareTo((OrderIndex(p.Second.ClassId), p.Second.Id)) < 0), $"b. {name}: records are not sorted by (build order, id)");

            // Build orders: one row per call with the earlier records passed; cobble blocks one at a time in shuffled order.
            var merged = new List<ScatterRecord>();
            var earlier = new List<ScatterRecord>();
            for (int r = 0; r < _table.BuildOrder.Count; r++)
            {
                if (_table.BuildOrder[r].BlockM == 0)
                {
                    var rows = Build(key, trees, earlier, r, r + 1, 0xFFFF).Records;
                    merged.AddRange(rows); earlier.AddRange(rows);
                }
                else
                {
                    int[] blocks = Enumerable.Range(0, 16).OrderBy(i => (i * 7 + 3) % 16).Reverse().ToArray();
                    foreach (int block in blocks) merged.AddRange(Build(key, trees, earlier, r, r + 1, 1 << block).Records);
                }
            }
            merged.Sort((x, y) => { int c = OrderIndex(x.ClassId).CompareTo(OrderIndex(y.ClassId)); return c != 0 ? c : x.Id.CompareTo(y.Id); });
            Check(Same(a, merged), $"b. {name}: row-by-row and shuffled-block build differs from the all-at-once build ({a.Count} vs {merged.Count} records)");

            // Blocks only partly built must not change what they contain.
            var someBlocks = Build(key, trees, a.Where(x => x.ClassId != 3).ToList(), 2, 3, 0x0A05).Records;
            var allCobbles = a.Where(x => x.ClassId == 3).ToList();
            Check(someBlocks.All(x => allCobbles.Contains(x)), $"b. {name}: a partial block mask returned a cobble the full build does not have");
            Check(someBlocks.Count == allCobbles.Count(x => ((0x0A05 >> BlockOf(key, x)) & 1) != 0), $"b. {name}: a partial block mask returned a different cobble count");
            Invariants(name, key, a);
            Detail($"b. {name}: {a.Count} records ({string.Join(", ", _table.BuildOrder.Select(r => $"class {r.Id}: {a.Count(x => x.ClassId == r.Id)}"))})");
        }

        // 1 versus 4 workers, each with its own sampler copy made on its own thread.
        var singles = new Dictionary<string, List<ScatterRecord>>();
        foreach (string name in FocusTiles)
        {
            Key key = KeyOf(T(name));
            TreeSet trees = Trees(key);
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            singles[name] = Build(key, trees, Array.Empty<ScatterRecord>(), 0, _table.BuildOrder.Count, 0xFFFF, copy).Records;
        }
        var parallel = new Dictionary<string, List<ScatterRecord>>();
        var tasks = FocusTiles.Select(name => Task.Run(() =>
        {
            Key key = KeyOf(T(name));
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            var records = Build(key, Trees(key), Array.Empty<ScatterRecord>(), 0, _table.BuildOrder.Count, 0xFFFF, copy).Records;
            lock (parallel) parallel[name] = records;
        })).ToArray();
        // Tree sets are cached on the main thread first (the tree catalog is main thread only).
        Task.WaitAll(tasks);
        foreach (string name in FocusTiles)
        {
            Check(parallel.ContainsKey(name) && Same(singles[name], parallel[name]), $"b. {name}: 4 workers differ from 1 worker");
            Check(Same(singles[name], reference[name]), $"b. {name}: the 1-worker build differs from the reference build");
        }
        Detail($"b. identity: 4 tiles x (2 runs, row-by-row with earlier records, shuffled blocks, partial block mask, 1 vs 4 workers) identical");
    }

    private int BlockOf(Key key, ScatterRecord r)
    {
        double a = r.AroundCm / 100.0, z = r.AcrossCm / 100.0;
        long cell = (long)Math.Floor(a / _aroundCell);
        int bx = (int)((cell - key.Around * 8) * 4 / 8), bz = (int)Math.Floor((z - key.Across * 240.0) / 60.0);
        return Math.Clamp(bz, 0, 3) * 4 + Math.Clamp(bx, 0, 3);
    }

    /// <summary>Spacing, Avoid and (in-tile) tree clearance hold for the accepted records.</summary>
    private void Invariants(string name, Key key, List<ScatterRecord> records)
    {
        var rows = _table.Rows.ToDictionary(r => r.Id);
        double Dist(ScatterRecord a, ScatterRecord b)
        {
            double da = (a.AroundCm - b.AroundCm) / 100.0, dz = (a.AcrossCm - b.AcrossCm) / 100.0;
            if (da > Circ * .5) da -= Circ; else if (da < -Circ * .5) da += Circ;
            return Math.Sqrt(da * da + dz * dz);
        }
        int spacingViolations = 0, avoidViolations = 0, treeViolations = 0;
        foreach (var group in records.GroupBy(r => r.ClassId))
        {
            RingScatterClass row = rows[group.Key];
            var list = group.ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (Dist(list[i], list[j]) < row.SpacingM + row.SpacingSizeFactor * (list[i].Size + list[j].Size) * 0.5 - 1e-9) spacingViolations++;
            foreach (RingScatterAvoid avoid in row.Avoid)
                foreach (ScatterRecord mine in list)
                    foreach (ScatterRecord other in records.Where(r => r.ClassId == avoid.ClassId))
                        if (Dist(mine, other) < avoid.ClearanceM + avoid.SizeFactor * (mine.Size + other.Size) * 0.5 - 1e-9) avoidViolations++;
        }
        var trees = RingBiomeDecorationLayer.BuildImportedForestReviewTile(_root, key.Around, key.Across, (long)Math.Ceiling(Circ / 30.0), _aroundCell, LandscapeWorld.Seed, Circ, Width);
        foreach (ScatterRecord rec in records)
        {
            RingScatterClass row = rows[rec.ClassId];
            double clearance = row.TreeClearanceM + row.TreeClearanceSizeFactor * rec.Size;
            foreach (var tree in trees)
            {
                double da = (tree.Around - rec.AroundCm / 100.0), dz = tree.Across - rec.AcrossCm / 100.0;
                if (da * da + dz * dz < clearance * clearance - 1e-9) treeViolations++;
            }
        }
        Check(spacingViolations == 0, $"b. {name}: {spacingViolations} same-class spacing violations");
        Check(avoidViolations == 0, $"b. {name}: {avoidViolations} Avoid violations");
        Check(treeViolations == 0, $"b. {name}: {treeViolations} tree clearance violations");
        Check(records.All(r => ScatterOwner(r) == key), $"b. {name}: a record lies outside its tile");
    }

    private Key ScatterOwner(ScatterRecord r) => _ctx.OwnerOf(r.AroundCm, r.AcrossCm);

    // ------------------------------------------------------------------ c. seams

    private void Seams()
    {
        Tile steep = T("steep_eye");
        long lastTile = _ctx.AroundTileCount - 1;
        var pairs = new List<(string Name, Key A, Key B, double Ca, double Cz, double Ha, double Hz)>
        {
            ("around seam", KeyOf(steep.TA, steep.TZ), KeyOf(steep.TA + 1, steep.TZ), (steep.TA + 1) * 8 * _aroundCell, steep.TZ * 240.0 + 120.0, 25.0, 120.0),
            ("across seam", KeyOf(steep.TA, steep.TZ), KeyOf(steep.TA, steep.TZ + 1), steep.TA * 8 * _aroundCell + 120.0, (steep.TZ + 1) * 240.0, 120.0, 25.0),
            ("ring seam", KeyOf(lastTile, steep.TZ), KeyOf(0, steep.TZ), 0.0, steep.TZ * 240.0 + 120.0, 25.0, 120.0),
        };
        // The steep tile has no trees; a forest tile exercises the halo tree path across the same kinds of seam.
        Tile forest = T("forest_edge_eye");
        pairs.Add(("forest around seam", KeyOf(forest.TA, forest.TZ), KeyOf(forest.TA + 1, forest.TZ), (forest.TA + 1) * 8 * _aroundCell, forest.TZ * 240.0 + 120.0, 25.0, 120.0));
        pairs.Add(("forest across seam", KeyOf(forest.TA, forest.TZ), KeyOf(forest.TA, forest.TZ + 1), forest.TA * 8 * _aroundCell + 120.0, (forest.TZ + 1) * 240.0, 120.0, 25.0));
        foreach (var (name, a, b, ca, cz, ha, hz) in pairs)
        {
            List<ScatterRecord> ra = All(a), rb = All(b);
            Check(ra.All(r => ScatterOwner(r) == a), $"c. {name}: a record of tile A is owned by another tile");
            Check(rb.All(r => ScatterOwner(r) == b), $"c. {name}: a record of tile B is owned by another tile");
            var ids = new HashSet<ulong>(ra.Select(r => r.Id));
            Check(!rb.Any(r => ids.Contains(r.Id)), $"c. {name}: a record id appears in both tiles");
            Check(ra.Concat(rb).Select(r => (r.AroundCm, r.AcrossCm, r.ClassId)).Distinct().Count() == ra.Count + rb.Count, $"c. {name}: a position is duplicated across the pair");

            Dictionary<ulong, bool> fromA, fromB;
            using (RingTerrainSampler copy = _root.CreateIndependentCopy()) fromA = _ctx.DecideCandidatesInRectangle(copy, a, Trees(a), ca, cz, ha, hz);
            using (RingTerrainSampler copy = _root.CreateIndependentCopy()) fromB = _ctx.DecideCandidatesInRectangle(copy, b, Trees(b), ca, cz, ha, hz);
            bool agree = fromA.Count == fromB.Count && fromA.All(kv => fromB.TryGetValue(kv.Key, out bool v) && v == kv.Value);
            Check(agree, $"c. {name}: a candidate within 25 m of the edge is decided differently from the two sides ({fromA.Count} vs {fromB.Count} candidates)");
            double Wrap(double d) => d > Circ * .5 ? d - Circ : d < -Circ * .5 ? d + Circ : d;
            var unionInStrip = ra.Concat(rb).Where(r => Math.Abs(Wrap(r.AroundCm / 100.0 - ca)) <= ha && Math.Abs(r.AcrossCm / 100.0 - cz) <= hz).Select(r => r.Id).ToHashSet();
            var acceptedInStrip = fromA.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
            Check(unionInStrip.SetEquals(acceptedInStrip), $"c. {name}: the records of the two tiles in the strip ({unionInStrip.Count}) differ from the decided candidates ({acceptedInStrip.Count})");
            Detail($"c. {name}: tiles ({a.Around},{a.Across}) and ({b.Around},{b.Across}): {ra.Count} + {rb.Count} records, {fromA.Count} candidates in the strip, {acceptedInStrip.Count} accepted, decided identically from both sides");
        }
    }

    // ------------------------------------------------------------------ d. file

    private void FileChecks(string cacheRoot)
    {
        Directory.CreateDirectory(cacheRoot);
        Tile tile = T("scree_eye");
        Key key = KeyOf(tile);
        List<ScatterRecord> records = All(key);
        Check(records.Count > 0, "d. the scree tile has no records to write");
        var data = new ScatterTileData(_table.BuildOrder.Count, 0xFFFF, records);
        ulong treeHash = RingBiomeDecorationLayer.VegetationCacheSettingsHash((long)Math.Ceiling(Circ / 30.0), Circ, 30f, Width, 8, 12, .72f, .20f, .70f, 32f, true);
        string identity = RingScatterTileFile.Identity(_root, _table, treeHash);
        string path = RingScatterTileFile.PathFor(cacheRoot, identity, key.Around, key.Across);
        Check(RingScatterTileFile.TryWrite(path, cacheRoot, 64L * 1024 * 1024, 0, key.Around, key.Across, _table, data), "d. the tile did not write");
        Check(File.Exists(path), "d. the written file is missing");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "d. a temporary file was left behind");
        Check(path.Replace('\\', '/').Contains($"/scatter/{identity}/r_{key.Around / 64}_{key.Across / 64}/s_{key.Around}_{key.Across}.bin"), "d. unexpected path shape: " + path);
        long expectedLength = 48 + records.Count * 44L + 8;
        Check(new FileInfo(path).Length == expectedLength, $"d. file length {new FileInfo(path).Length}, expected {expectedLength}");
        Check(RingScatterTileFile.TryLoad(path, key.Around, key.Across, _table, out ScatterTileData? loaded) && loaded != null
            && loaded.BuiltRows == data.BuiltRows && loaded.CobbleBlockMask == data.CobbleBlockMask && Same(loaded.Records, records), "d. the tile did not reload byte-identically");
        byte[] original = File.ReadAllBytes(path);
        Check(original.AsSpan().SequenceEqual(RingScatterTileFile.Serialize(key.Around, key.Across, RingBiomeDecorationLayer.ScatterAlgorithmVersion, _table, loaded!)), "d. re-serialising the loaded tile differs from the file");

        bool Rejects(byte[] bytes, string label)
        {
            File.WriteAllBytes(path, bytes);
            bool loadedOk = RingScatterTileFile.TryLoad(path, key.Around, key.Across, _table, out _);
            Check(!loadedOk, "d. a corrupted file was accepted: " + label);
            return !loadedOk;
        }
        byte[] Flip(int offset, byte value) { var copy = (byte[])original.Clone(); copy[offset] = value; return copy; }
        byte[] Retrail(byte[] bytes) { BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - 8), RingScatterTileFile.Fnv1a64(bytes.AsSpan(0, bytes.Length - 8))); return bytes; }
        Rejects(Flip(0, (byte)(original[0] ^ 0xFF)), "magic");
        Rejects(Flip(4, (byte)(original[4] + 1)), "cache version");
        Rejects(Flip(16, (byte)(original[16] ^ 0x01)), "header key across");
        Rejects(Retrail(Flip(24, (byte)(original[24] + 1))), "algorithm version (trailer fixed)");
        Rejects(Flip(original.Length - 1, (byte)(original[^1] ^ 0xFF)), "trailer");
        Rejects(Flip(48 + 5, (byte)(original[48 + 5] ^ 0x10)), "a record byte (trailer stale)");
        Rejects(Retrail(Flip(48 + 24, 0xFF)), "record class id (trailer fixed)");
        Rejects(Retrail(Flip(48 + 26, 0xFF)), "record mesh index (trailer fixed)");
        var nan = (byte[])original.Clone(); BitConverter.TryWriteBytes(nan.AsSpan(48 + 28), float.NaN);
        Rejects(Retrail(nan), "non-finite size (trailer fixed)");
        Rejects(original.AsSpan(0, original.Length - 1).ToArray(), "a truncated file");
        File.WriteAllBytes(path, original);
        Check(RingScatterTileFile.TryLoad(path, key.Around, key.Across, _table, out _), "d. the restored file no longer loads");
        Check(!RingScatterTileFile.TryLoad(path, key.Around + 1, key.Across, _table, out _), "d. a file loaded under another tile key");
        Check(!RingScatterTileFile.TryLoad(path, key.Around, key.Across, _table, out _, RingBiomeDecorationLayer.ScatterAlgorithmVersion + 1), "d. a file loaded under another algorithm version");
        Check(!RingScatterTileFile.TryLoad(path, key.Around, key.Across, RingScatterClassTable.CreateForTests(_table.Rows.Take(2)), out _), "d. a file loaded under another class table");

        // A bumped ScatterAlgorithmVersion changes the identity and so the path.
        string bumped = RingScatterTileFile.Identity(_root, _table, treeHash, RingBiomeDecorationLayer.ScatterAlgorithmVersion + 1);
        Check(bumped != identity && RingScatterTileFile.PathFor(cacheRoot, bumped, key.Around, key.Across) != path, "d. a bumped ScatterAlgorithmVersion did not change the path");
        Check(RingScatterTileFile.Identity(_root, RingScatterClassTable.Production, treeHash) != identity, "d. the class table hash is not in the identity");
        Check(RingScatterTileFile.Identity(_root, _table, treeHash + 1) != identity, "d. the tree settings hash is not in the identity");
        Check(RingScatterTileFile.Identity(_root, _table, treeHash) == identity, "d. the identity is not stable");
        Detail($"d. file: {records.Count} records, {expectedLength} bytes, identity {identity}; 10 corruptions rejected; bumped version -> {bumped}");
    }

    // ------------------------------------------------------------------ e. disk gate

    private void DiskGate()
    {
        int mask = _root.WorldFeatureMask;
        Check(!RingScatterTileFile.DiskCacheAllowed(mask, WorldGenerationLedger.Production, false), "e. DiskCacheAllowed is true for the review world (scatter.rocks is open)");
        Check(RingScatterTileFile.DiskCacheAllowed(mask, WorldGenerationLedger.Production, true), "e. the test override did not enable the disk cache");
        Check(!RingScatterTileFile.DiskCacheAllowed(0, WorldGenerationLedger.Production, false), "e. DiskCacheAllowed is true with no scatter feature in the mask");
        var frozen = WorldGenerationLedger.CreateForTests(
            new[] { new WorldGenerationLedger.Row(1, "baseline", new[] { "scatter.rocks" }, "test") },
            new[] { new WorldGenerationLedger.FeatureInfo("scatter.rocks", 4, "T5", 1), new WorldGenerationLedger.FeatureInfo("scatter.river", 5, "T5", 0) });
        Check(RingScatterTileFile.DiskCacheAllowed(1 << 4, frozen, false), "e. a frozen scatter.rocks did not allow the disk cache");
        Check(!RingScatterTileFile.DiskCacheAllowed((1 << 4) | (1 << 5), frozen, false), "e. an open scatter.river alongside a frozen scatter.rocks allowed the disk cache");
        RingScatterTileFile.ScatterDiskCacheForTests = true;
        Check(RingScatterTileFile.ScatterDiskCacheForTests && RingScatterTileFile.DiskCacheAllowed(mask, WorldGenerationLedger.Production, RingScatterTileFile.ScatterDiskCacheForTests), "e. the ScatterDiskCacheForTests hook did not enable the cache");
        RingScatterTileFile.ScatterDiskCacheForTests = false;
        Detail("e. disk gate: false for the open review world, true with the override, true for a frozen ledger, false with an open sibling feature");
    }

    // ------------------------------------------------------------------ f. feature off

    private void FeatureOff()
    {
        LandscapeWorld.Spec gen1 = LandscapeWorld.For(1);
        using RingTerrainSampler sampler = gen1.CreateSampler();
        Check((sampler.WorldFeatureMask & (1 << 4)) == 0, "f. the generation 1 world has bit 4");
        Check(!RingHabitatFieldGrid.IsEnabled(sampler.WorldFeatureMask), "f. RingHabitatFieldGrid.IsEnabled is true without scatter.rocks");
        Check(!WorldGenerationFingerprint.ScatterEnabled(gen1), "f. the fingerprint thinks generation 1 has scatter.rocks");
        Check(WorldGenerationFingerprint.ScatterEnabled(_spec), "f. the fingerprint does not see scatter.rocks in the review world");
        int callsBefore = RingBiomeDecorationLayer.ScatterRowsBuildCalls;
        var row = new Dictionary<string, object?>();
        WorldGenerationFingerprint.ComputeScatterSlot(gen1, null!, row);
        Check(RingBiomeDecorationLayer.ScatterRowsBuildCalls == callsBefore, "f. BuildScatterRows was reached on the generation 1 path");
        Check(row["scatter"] is Dictionary<string, object?> slot && (int)slot["count"]! == 0 && (string)slot["hash"]! == "none", "f. the generation 1 scatter slot is not {count 0, hash none}");
        Check(!row.ContainsKey("scatter_params"), "f. the generation 1 row has scatter_params");

        // The review world's slot: the production table has no rows, so every tile is empty and the build is never entered.
        var review = new Dictionary<string, object?>();
        LandscapeWorld.Worker worker = _spec.CreateWorkers(1)[0];
        WorldGenerationFingerprint.ComputeScatterSlot(_spec, worker, review);
        Check(review["scatter"] is List<object?> tiles && tiles.Count == 10, "f. the review slot does not hold 10 tiles");
        Check(review.ContainsKey("scatter_params"), "f. the review slot lacks scatter_params");
        Check(RingBiomeDecorationLayer.BuildScatterReviewTile(_root, 0, 0, LandscapeWorld.Seed, Circ, Width, RingScatterClassTable.Production).Count == 0, "f. the empty production table produced records");
        worker.Dispose();
        Detail("f. feature off: no bit 4, grid disabled, fingerprint slot none, BuildScatterRows never reached; the review slot has 10 tiles (production table: no rows yet)");
    }

    // ------------------------------------------------------------------ g. trees untouched

    private void TreesUntouched()
    {
        using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
        using RingTerrainSampler review = LandscapeWorld.For(1, new[] { "scatter.rocks" }).CreateSampler();
        Check((review.WorldFeatureMask & (1 << 4)) != 0 && (gen1.WorldFeatureMask & (1 << 4)) == 0, "g. the two worlds do not differ in bit 4");
        long cells = (long)Math.Ceiling(Circ / 30.0);
        int total = 0, differing = 0;
        foreach (Tile tile in _tiles)
        {
            var a = RingBiomeDecorationLayer.BuildImportedForestReviewTile(gen1, tile.TA, tile.TZ, cells, _aroundCell, LandscapeWorld.Seed, Circ, Width);
            var b = RingBiomeDecorationLayer.BuildImportedForestReviewTile(review, tile.TA, tile.TZ, cells, _aroundCell, LandscapeWorld.Seed, Circ, Width);
            total += a.Count;
            if (!a.SequenceEqual(b)) { differing++; Check(false, $"g. {tile.Name}: tree records differ between generation 1 and the scatter review world"); }
        }
        Detail($"g. trees untouched: {total} tree records over 14 tiles, {differing} tiles differ between generation 1 and the review world");
    }

    // ------------------------------------------------------------------ h. halo tree mirror

    private void HaloMirror()
    {
        int checkedTotal = 0, mismatchTotal = 0;
        foreach (string name in new[] { "steep_eye", "forest_edge_eye", "forest_interior_eye" })
        {
            Key key = KeyOf(T(name));
            TreeSet trees = Trees(key);
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            var (checkedCount, mismatches, samples) = _ctx.CheckHaloTreeMirror(copy, key, trees);
            checkedTotal += checkedCount; mismatchTotal += mismatches;
            Detail($"h. {name}: {trees.Count} trees in the tile, {checkedCount} candidates checked, {mismatches} mismatches ({100.0 * (checkedCount - mismatches) / checkedCount:F3} % agreement)" + (samples.Count > 0 ? "\n    " + string.Join("\n    ", samples) : ""));
        }
        Check(mismatchTotal == 0, $"h. the halo tree mirror disagrees with BuildVegetationTile on {mismatchTotal} of {checkedTotal} candidates");
    }

    // ------------------------------------------------------------------ i. ordering

    private void Ordering()
    {
        // Reach descending, ties by id: ids (7, 3, 5, 4) with reach (600, 2500, 600, 600) build as 3, 4, 5, 7.
        var table = RingScatterClassTable.CreateForTests(new[]
        {
            new RingScatterClass { Id = 7, ReachM = 600f }, new RingScatterClass { Id = 3, ReachM = 2500f }, new RingScatterClass { Id = 5, ReachM = 600f }, new RingScatterClass { Id = 4, ReachM = 600f },
        });
        string order = string.Join(",", table.BuildOrder.Select(r => r.Id));
        Check(order == "3,4,5,7", "i. build order was " + order + ", expected 3,4,5,7");
        Check(string.Join(",", _table.BuildOrder.Select(r => r.Id)) == "1,2,3", "i. the test table's build order is not 1,2,3");
        // A table whose id order differs from its build order still builds identically (ids only name rows).
        var renumbered = BuildTestTable(mid: 9, longId: 5, block: 2);
        Check(string.Join(",", renumbered.BuildOrder.Select(r => r.Id)) == "5,9,2", "i. renumbered build order was " + string.Join(",", renumbered.BuildOrder.Select(r => r.Id)));
        Check(renumbered.Validate().Count == 0, "i. the renumbered table does not validate");
        Detail($"i. ordering: ids (7,3,5,4) with reach (600,2500,600,600) -> {order}; test table ids renumbered (5,9,2) -> 5,9,2");
    }

    // ------------------------------------------------------------------ j. cost

    private void Cost()
    {
        var lines = new List<string>();
        // Warm-up (JIT) on a throwaway tile.
        { Key k = KeyOf(T("plains_eye")); Build(k, Trees(k), Array.Empty<ScatterRecord>(), 0, 3, 0xFFFF); }
        double sumO = 0, sumB = 0, sumC = 0;
        foreach (Tile tile in _tiles)
        {
            Key key = KeyOf(tile);
            TreeSet trees = Trees(key);
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            var earlier = new List<ScatterRecord>();
            var o = Build(key, trees, earlier, 0, 1, 0xFFFF, copy); earlier.AddRange(o.Records);
            var b = Build(key, trees, earlier, 1, 2, 0xFFFF, copy); earlier.AddRange(b.Records);
            var c = Build(key, trees, earlier, 2, 3, 0xFFFF, copy);
            sumO += o.OutcropMs; sumB += b.BoulderMs; sumC += c.CobbleMs;
            lines.Add($"  {tile.Name,-22} O {o.OutcropMs,7:F2} ms ({o.Records.Count,4})  B {b.BoulderMs,7:F2} ms ({b.Records.Count,4})  C {c.CobbleMs,7:F2} ms ({c.Records.Count,4})   grid fine {b.FineEvaluations,5} coarse {b.CoarseEvaluations,4} climate {b.ClimateSamples,4}");
        }
        Detail("j. cost per tile (stage O = row 1, B = row 2, C = row 3 over all 16 blocks; each stage from the previous stage's records, one shared sampler copy; test table, this machine):\n" + string.Join("\n", lines));
        Detail($"j. totals over 14 tiles: O {sumO:F1} ms, B {sumB:F1} ms, C {sumC:F1} ms");
        foreach (string name in new[] { "steep_eye", "scree_eye" })
        {
            int count = All(KeyOf(T(name))).Count;
            Check(count >= 100, $"j. {name} has only {count} records; the test table should give the steep and scree tiles hundreds");
        }
    }
}
