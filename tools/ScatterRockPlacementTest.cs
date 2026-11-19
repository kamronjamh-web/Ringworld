using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using Ctx = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterBuildContext;
using Key = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTileKey;
using TreeSet = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTreeSet;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T5-5 (docs/work-orders/terrain-vegetation/T5.md, design A2/A5): the behaviour of the production rock rows (outcrops, boulders, cobble clusters) on the scatter review world.
/// Outcrops rise with rock exposure and are absent on plains; boulders prefer hollows at equal slope; cobbles sit on scree; nothing on water, site masks, rims or trunks; outcrops agree with the
/// terrain's 4 m rock exposure; resident counts around steep_eye stay under the C1 caps; plains keep some lone boulders. Prints SCATTER_ROCK_PLACEMENT=PASS.
/// Headless: tools/run_tests.ps1 -Tests scatter_rock_placement_test. With the user arg --png it also writes the debug plot of steep_eye and breaks_300m.
/// </summary>
public partial class ScatterRockPlacementTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const string DressingFixture = "res://tools/fixtures/landscape_poses_dressing_s1337.json";
    private static readonly string[] DressingNames = { "steep_eye", "scree_eye", "cliff_eye", "breaks_300m", "mountain_2km", "lowland_eye" };
    private const string PngPath = "c:/Projects/Ringworld/.godot/audit_baseline/common/t5/t5-5_rocks_steep_breaks.png";
    private const ushort Outcrop = 1, Boulder = 2, Cobble = 3;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("SCATTER_ROCK_PLACEMENT_DETAIL " + text);

    private sealed class TileData
    {
        public string Name = "";
        public Key Key;
        public double A0, A1, Z0, Z1;
        public TreeSet Trees = null!;
        public List<ScatterRecord> Records = new();
        public RingBiomeDecorationLayer.ScatterRowsResult Result = null!;
        public double Hectares => (A1 - A0) * (Z1 - Z0) / 10000.0;
    }

    private LandscapeWorld.Spec _spec = null!;
    private RingTerrainSampler _root = null!;
    private RingTerrainSampler _probe = null!;
    private RingHabitatFieldGrid _grid = null!;
    private Ctx _ctx = null!;
    private RingScatterClassTable _table = RingScatterClassTable.Production.ForMask(1 << 4);
    private double _aroundCell, _tileA;
    private readonly List<TileData> _tiles = new();
    private readonly Dictionary<Key, List<ScatterRecord>> _outcropsByTile = new();
    private (double Around, double Across) _steepPose;

    public int Run()
    {
        try
        {
            Setup();
            BuildPoseTiles();
            Caps();
            BuriedBases();
            Outcrops();
            Boulders();
            Cobbles();
            Exclusions();
            GroundAgreement();
            Summary();
            Plains();
            if (OS.GetCmdlineUserArgs().Contains("--png")) Plot();
        }
        catch (Exception e) { Console.Error.WriteLine("SCATTER_ROCK_PLACEMENT=FAIL exception " + e); return 1; }
        finally { _probe?.Dispose(); _root?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("SCATTER_ROCK_PLACEMENT=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("SCATTER_ROCK_PLACEMENT=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ setup

    private void Setup()
    {
        _spec = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" });
        _root = _spec.CreateSampler();
        Check(_table.Validate().Count == 0 && _table.Rows.Count == 3, "the production table is not the valid three-row rock table");
        _ctx = Ctx.ForReview(LandscapeWorld.Seed, Circ, Width, _table, null, _root.WorldFeatureMask);
        _aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        _tileA = _ctx.TileCells * _ctx.AroundCellMeters;
        _probe = _root.CreateIndependentCopy();
        _grid = new RingHabitatFieldGrid(_probe, _ctx.Edits, LandscapeWorld.Seed, Circ, Width);
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        var poses = new List<LandscapePose>();
        foreach (string name in WorldGenerationFingerprint.TilePoses) poses.Add(t0.Poses.First(p => p.Name == name));
        foreach (string name in DressingNames) poses.Add(dressing.Poses.First(p => p.Name == name));
        foreach (LandscapePose p in poses)
        {
            long ta = (long)Math.Floor(Math.Floor(p.Around / _aroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            _tiles.Add(MakeTile(p.Name, ta, tz));
        }
        Check(_tiles.Count == 14, "expected 14 test tiles, got " + _tiles.Count);
        LandscapePose steep = poses.First(p => p.Name == "steep_eye");
        _steepPose = (steep.Around, steep.Across);
    }

    private TileData MakeTile(string name, long ta, long tz) => new()
    {
        Name = name, Key = new Key(ta, tz), A0 = ta * _tileA, A1 = (ta + 1) * _tileA, Z0 = tz * 240.0, Z1 = (tz + 1) * 240.0
    };

    private TileData T(string name) => _tiles.First(t => t.Name == name);

    private void BuildPoseTiles()
    {
        var timer = Stopwatch.StartNew();
        foreach (TileData t in _tiles)
        {
            t.Trees = _ctx.BuildTreeSet(_root, t.Key);
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            t.Result = RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, t.Key, t.Trees, Array.Empty<ScatterRecord>(), 0, _table.BuildOrder.Count, 0xFFFF);
            t.Records = t.Result.Records;
            _outcropsByTile[t.Key] = t.Records.Where(r => r.ClassId == Outcrop).ToList();
        }
        // The helper the fingerprint uses gives the same records as the direct build.
        TileData s = T("steep_eye");
        var viaHelper = RingBiomeDecorationLayer.BuildScatterReviewTile(_root, s.Key.Around, s.Key.Across, LandscapeWorld.Seed, Circ, Width, _table);
        Check(viaHelper.SequenceEqual(s.Records), "BuildScatterReviewTile differs from the direct build on steep_eye");
        Detail($"setup: 14 pose tiles built in {timer.Elapsed.TotalSeconds:F1} s; tile {_tileA:F2} m x 240 m = {s.Hectares:F2} ha; table hash {_table.Hash:X16}");
    }

    // ------------------------------------------------------------------ shared sampling

    private RingHabitatSample At(ScatterRecord r) => _grid.Sample(r.AroundCm / 100.0, r.AcrossCm / 100.0);

    private IEnumerable<RingHabitatSample> Lattice(TileData t, double stepM = 4.0)
    {
        _grid.PrepareWindow(t.A0 - 16.0, t.A1 + 16.0, t.Z0 - 16.0, t.Z1 + 16.0);
        return Lattice(t.A0, t.A1, t.Z0, t.Z1, stepM);
    }

    private IEnumerable<RingHabitatSample> Lattice(double a0, double a1, double z0, double z1, double stepM)
    {
        for (double a = a0 + stepM * 0.5; a < a1; a += stepM)
            for (double z = z0 + stepM * 0.5; z < z1; z += stepM)
            {
                if (z < 4.0 || z > Width - 4.0) continue;
                RingHabitatSample f = _grid.Sample(a, z);
                if ((f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) != 0) continue;
                yield return f;
            }
    }

    private const double LatticeCellHa = 16.0 / 10000.0;
    private readonly List<(Key Key, double A0, double Z0)> _discTiles = new();

    // ------------------------------------------------------------------ a. outcrops

    private void Outcrops()
    {
        foreach (string name in new[] { "plains_eye", "coast_eye", "lowland_eye" })
        {
            int n = T(name).Records.Count(r => r.ClassId == Outcrop);
            Check(n == 0, $"a. {name}: {n} outcrops on a plains tile");
        }
        double[] edges = { 0.0, 0.2, 0.5, 1.0000001 };
        var area = new double[3]; var count = new int[3];
        foreach (TileData t in _tiles)
        {
            foreach (RingHabitatSample f in Lattice(t)) area[BinOf(f.RockExposure, edges)] += LatticeCellHa;
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId == Outcrop)) count[BinOf(At(r).RockExposure, edges)]++;
        }
        // The 14 tiles hold only a handful of outcrops (0.3 per ha at full response), so the outcrop tiles of the 2.5 km disc around steep_eye join the statistics (16 m lattice).
        foreach (var (key, a0, z0) in _discTiles)
        {
            foreach (RingHabitatSample f in Lattice(a0, a0 + _tileA, z0, z0 + 240.0, 16.0)) area[BinOf(f.RockExposure, edges)] += 256.0 / 10000.0;
            foreach (ScatterRecord r in _outcropsByTile[key]) count[BinOf(At(r).RockExposure, edges)]++;
        }
        var lines = new List<string>();
        var dens = new double[3];
        for (int b = 0; b < 3; b++)
        {
            dens[b] = area[b] > 0 ? count[b] / area[b] : double.NaN;
            lines.Add($"  RockExposure {edges[b]:F1}-{Math.Min(1.0, edges[b + 1]):F1}: {area[b],8:F2} ha, {count[b],3} outcrops, {dens[b],6:F2} per ha");
        }
        Detail($"a. outcrop density by rock exposure at the root (14 pose tiles on a 4 m lattice plus {_discTiles.Count} outcrop tiles of the 2.5 km steep_eye disc on a 16 m lattice):\n" + string.Join("\n", lines));
        Check(dens[0] <= dens[1] && dens[1] <= dens[2] && dens[2] > dens[0], $"a. outcrop density does not rise with rock exposure ({dens[0]:F2}, {dens[1]:F2}, {dens[2]:F2})");
        Detail($"a. outcrops on plains_eye / coast_eye / lowland_eye: {string.Join(", ", new[] { "plains_eye", "coast_eye", "lowland_eye" }.Select(n => T(n).Records.Count(r => r.ClassId == Outcrop)))}; total outcrops in the statistics {count.Sum()}");
    }

    private static int BinOf(double v, double[] edges)
    {
        for (int b = 0; b < edges.Length - 1; b++) if (v < edges[b + 1]) return b;
        return edges.Length - 2;
    }

    // ------------------------------------------------------------------ b. boulders

    private void Boulders()
    {
        // Angle of repose (T5-7b): the boulder row's slope multiplier reaches 0 at 52 degrees of grid slope, so no boulder root stands on a steeper face.
        int boulderCount = 0, steepest = 0; double steepestSlope = 0.0;
        foreach (TileData t in _tiles)
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId == Boulder)) { boulderCount++; double sl = At(r).SlopeDeg; if (sl > steepestSlope) { steepestSlope = sl; steepest++; } }
        Check(steepestSlope <= 52.5, $"b. a boulder root stands on a {steepestSlope:F1} degree face (limit 52)");
        Detail($"b. angle of repose: {boulderCount} boulders over the 14 tiles, steepest root {steepestSlope:F1} degrees");
        const int bands = 18;
        var area = new double[bands, 2]; var samples = new int[bands, 2]; var count = new int[bands, 2];
        foreach (TileData t in _tiles)
        {
            foreach (RingHabitatSample f in Lattice(t))
            {
                int side = Side(f.Relief64);
                if (side < 0) continue;
                int band = Math.Clamp((int)(f.SlopeDeg / 5.0), 0, bands - 1);
                area[band, side] += LatticeCellHa; samples[band, side]++;
            }
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId == Boulder))
            {
                RingHabitatSample f = At(r);
                int side = Side(f.Relief64);
                if (side < 0) continue;
                count[Math.Clamp((int)(f.SlopeDeg / 5.0), 0, bands - 1), side]++;
            }
        }
        var lines = new List<string>();
        double cArea = 0, vArea = 0; int cCount = 0, vCount = 0, bandsOk = 0, bandsUsed = 0;
        for (int b = 0; b < bands; b++)
        {
            if (samples[b, 0] < 30 || samples[b, 1] < 30) { if (samples[b, 0] + samples[b, 1] > 0) lines.Add($"  slope {b * 5,2}-{b * 5 + 5,2} deg: skipped (concave samples {samples[b, 0]}, convex samples {samples[b, 1]})"); continue; }
            double dc = count[b, 0] / area[b, 0], dv = count[b, 1] / area[b, 1];
            double ratio = dv > 0 ? dc / dv : (dc > 0 ? double.PositiveInfinity : 1.0);
            bandsUsed++; if (ratio >= 2.0) bandsOk++;
            cArea += area[b, 0]; vArea += area[b, 1]; cCount += count[b, 0]; vCount += count[b, 1];
            lines.Add($"  slope {b * 5,2}-{b * 5 + 5,2} deg: concave {area[b, 0],7:F2} ha {count[b, 0],4} boulders {dc,7:F1}/ha | convex {area[b, 1],7:F2} ha {count[b, 1],4} boulders {dv,7:F1}/ha | ratio {(double.IsInfinity(ratio) ? "inf" : ratio.ToString("F1"))}");
        }
        double oc = cCount / cArea, ov = vCount / vArea, overall = ov > 0 ? oc / ov : (oc > 0 ? double.PositiveInfinity : 1.0);
        Detail("b. boulder density, concave (Relief64 < -0.15) against convex (> +0.15) at equal slope (4 m lattice areas):\n" + string.Join("\n", lines));
        Detail($"b. pooled over {bandsUsed} bands: concave {oc:F1}/ha ({cCount}), convex {ov:F1}/ha ({vCount}), ratio {(double.IsInfinity(overall) ? "inf" : overall.ToString("F2"))}; {bandsOk} bands at 2x or more");
        Check(bandsUsed >= 2 && overall >= 2.0 && bandsOk >= 2, $"b. boulder concavity: pooled ratio {overall:F2} over {bandsUsed} bands, {bandsOk} bands at 2x");
        Check(cCount >= 20, $"b. only {cCount} boulders in concave cells; the check is too thin");
    }

    private static int Side(double relief64) => relief64 < -0.15 ? 0 : relief64 > 0.15 ? 1 : -1;

    // ------------------------------------------------------------------ c. cobbles

    private void Cobbles()
    {
        var hist = new int[11];
        int total = 0, inBand = 0, noTerm = 0;
        foreach (TileData t in _tiles)
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId == Cobble))
            {
                RingHabitatSample f = At(r);
                total++;
                if (!(f.Scree > 0.05 || f.RockExposure > 0.05)) noTerm++;
                double slope = f.SlopeDeg + RingGroundSurfaceModel.SlopeBiasDegrees(f.SlopeBaselineM);
                hist[Math.Clamp((int)(slope / 6.0), 0, 10)]++;
                if (slope >= 24.0 && slope <= 42.0) inBand++;
            }
        Detail($"c. cobble clusters over 14 tiles: {total}; slope (16 m + bias) histogram in 6 degree bins: " + string.Join(" ", hist.Select((n, i) => $"{i * 6}-{i * 6 + 6}:{n}").Where((s, i) => hist[i] > 0)));
        Detail($"c. {inBand} of {total} in [24, 42] degrees ({(total > 0 ? 100.0 * inBand / total : 0):F1} %), {noTerm} with neither Scree nor RockExposure above 0.05");
        Check(total >= 50, $"c. only {total} cobble clusters; the check is too thin");
        Check(noTerm == 0, $"c. {noTerm} cobble clusters have Scree and RockExposure both at most 0.05");
        Check(total > 0 && inBand >= 0.9 * total, $"c. only {inBand} of {total} cobble roots lie in the 24-42 degree band");
    }

    // ------------------------------------------------------------------ d. exclusions

    private void Exclusions()
    {
        var rows = _table.Rows.ToDictionary(r => r.Id);
        long cells = (long)Math.Ceiling(Circ / 30.0);
        int water = 0, blocked = 0, rim = 0, trunk = 0, pointWater = 0, total = 0, treesSeen = 0;
        foreach (TileData t in _tiles)
        {
            var trees = RingBiomeDecorationLayer.BuildImportedForestReviewTile(_root, t.Key.Around, t.Key.Across, cells, _aroundCell, LandscapeWorld.Seed, Circ, Width);
            treesSeen += trees.Count;
            foreach (ScatterRecord r in t.Records)
            {
                total++;
                RingHabitatSample f = At(r);
                if ((f.Flags & RingHabitatFlags.Water) != 0) water++;
                if ((f.Flags & RingHabitatFlags.SiteBlocked) != 0) blocked++;
                double a = r.AroundCm / 100.0, z = r.AcrossCm / 100.0;
                if (z < 4.0 || z > Width - 4.0) rim++;
                float h = RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a, z);
                TerrainSample s = _ctx.Edits.GetPresentationTerrainSample(_probe, (long)Math.Round(a), (int)Math.Round(z));
                if (RingBiomeDecorationLayer.IsSurfaceWater(_ctx.Edits, _probe, s, h) || s.SiteMask.BlocksVegetation) pointWater++;
                RingScatterClass row = rows[r.ClassId];
                double clearance = row.TreeClearanceM + row.TreeClearanceSizeFactor * r.Size;
                foreach (var tree in trees)
                {
                    double da = tree.Around - a, dz = tree.Across - z;
                    if (da * da + dz * dz < clearance * clearance - 1e-9) { trunk++; break; }
                }
            }
        }
        Detail($"d. exclusions over {total} records in 14 tiles ({treesSeen} in-tile trees): water {water}, site-blocked {blocked}, within 4 m of a rim {rim}, inside tree clearance {trunk}, water or site mask at the root point itself {pointWater}");
        Check(water == 0 && blocked == 0, $"d. {water} records on water and {blocked} on site-blocked nodes");
        Check(rim == 0, $"d. {rim} records within 4 m of a rim");
        Check(trunk == 0, $"d. {trunk} records within their tree clearance of an in-tile tree");
        Check(pointWater == 0, $"d. {pointWater} records whose own root point is water or site-blocked");
    }

    // ------------------------------------------------------------------ f. caps (C1)

    private double WrapD(double d) => d > Circ * .5 ? d - Circ : d < -Circ * .5 ? d + Circ : d;

    private double TileDistance(long ta, long tz)
    {
        double a0 = ta * _tileA, a1 = a0 + _tileA, z0 = tz * 240.0, z1 = z0 + 240.0;
        double da = Math.Max(Math.Max(a0 - _steepPose.Around, 0.0), _steepPose.Around - a1);
        double dz = Math.Max(Math.Max(z0 - _steepPose.Across, 0.0), _steepPose.Across - z1);
        return Math.Sqrt(da * da + dz * dz);
    }

    private void Caps()
    {
        const double outcropR = 2500.0;
        var timer = Stopwatch.StartNew();
        long ta0 = (long)Math.Floor((_steepPose.Around - outcropR) / _tileA), ta1 = (long)Math.Floor((_steepPose.Around + outcropR) / _tileA);
        long tz0 = Math.Max(0L, (long)Math.Floor((_steepPose.Across - outcropR) / 240.0)), tz1 = Math.Min((Width - 1) / 240, (long)Math.Floor((_steepPose.Across + outcropR) / 240.0));
        int outcropTiles = 0, boulderTiles = 0, cobbleTiles = 0;
        int outcrops = 0, outcropsWholeTiles = 0, boulders = 0, bouldersWholeTiles = 0, cobbles = 0, cobblesWholeTiles = 0;
        double Dist(ScatterRecord r)
        {
            double da = WrapD(r.AroundCm / 100.0 - _steepPose.Around), dz = r.AcrossCm / 100.0 - _steepPose.Across;
            return Math.Sqrt(da * da + dz * dz);
        }
        for (long ta = ta0; ta <= ta1; ta++)
            for (long tz = tz0; tz <= tz1; tz++)
            {
                double d = TileDistance(ta, tz);
                if (d > outcropR) continue;
                int rowTo = d < 100.0 ? 3 : d < 600.0 ? 2 : 1;
                var key = new Key(PositiveModulo(ta, _ctx.AroundTileCount), tz);
                List<ScatterRecord> records;
                TileData known = _tiles.FirstOrDefault(t => t.Key == key);
                if (known != null) records = known.Records;
                else
                {
                    TreeSet trees = _ctx.BuildTreeSet(_root, key);
                    using RingTerrainSampler copy = _root.CreateIndependentCopy();
                    records = RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, key, trees, Array.Empty<ScatterRecord>(), 0, rowTo, 0xFFFF).Records;
                    _outcropsByTile[key] = records.Where(r => r.ClassId == Outcrop).ToList();
                }
                if (known == null) _discTiles.Add((key, ta * _tileA, tz * 240.0));
                outcropTiles++; if (d < 600.0) boulderTiles++; if (d < 100.0) cobbleTiles++;
                foreach (ScatterRecord r in records)
                {
                    double dr = Dist(r);
                    if (r.ClassId == Outcrop) { outcropsWholeTiles++; if (dr <= 2500.0) outcrops++; }
                    else if (r.ClassId == Boulder && d < 600.0) { bouldersWholeTiles++; if (dr <= 600.0) boulders++; }
                    else if (r.ClassId == Cobble && d < 100.0) { cobblesWholeTiles++; if (dr <= 100.0) cobbles++; }
                }
            }
        Detail($"f. caps around steep_eye ({_steepPose.Around:F0}, {_steepPose.Across:F0}), {timer.Elapsed.TotalSeconds:F1} s: outcrops within 2500 m {outcrops} (cap 150; {outcropsWholeTiles} in the {outcropTiles} touching tiles), boulders within 600 m {boulders} (cap 1500; {bouldersWholeTiles} in {boulderTiles} tiles), cobble clusters within 100 m {cobbles} (cap 1000; {cobblesWholeTiles} in {cobbleTiles} tiles)");
        Check(outcrops <= 150, $"f. {outcrops} outcrops within 2.5 km of steep_eye exceed the cap of 150");
        // Since 2026-11-19 the boulder row carries the design-A5 density (user's choice) and the C1 caps are read as DRAWN (user, T5.md 2026-11-19): the GPU slot cap of 1,500 draws the
        // nearest boulders first, so the resident count may exceed it. Guard only against a runaway density (2.5x the cap).
        Check(boulders <= 3750, $"f. {boulders} boulders within 600 m of steep_eye exceed 2.5x the drawn cap of 1500");
        Check(cobbles <= 1000, $"f. {cobbles} cobble clusters within 100 m of steep_eye exceed the cap of 1000");
    }

    private static long PositiveModulo(long v, long n) { long r = v % n; return r < 0 ? r + n : r; }

    // ------------------------------------------------------------------ i. buried bases (T5-10b)

    /// <summary>The buried column of an outcrop (0.75 x size below the root) stays under the terrain at the centre and on a 16-point footprint ring, for every accepted outcrop in the 14 tiles and the steep_eye disc.</summary>
    private void BuriedBases()
    {
        RingScatterClass row = _table.Rows.First(r => r.Id == Outcrop);
        int checkedCount = 0, exposed = 0;
        double worst = double.MaxValue;
        string firstBad = "";
        foreach (ScatterRecord r in _outcropsByTile.Values.SelectMany(list => list))
        {
            double a = r.AroundCm / 100.0, z = r.AcrossCm / 100.0;
            double bottom = r.SurfaceHeight - row.BuriedDepthFraction * (double)r.Size + row.BuriedClearanceM, radius = row.BuriedFootprintRadius * (double)r.Size;
            double lowest = RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a, z) - bottom;
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8.0, pa = a + radius * Math.Cos(angle), pz = Math.Clamp(z + radius * Math.Sin(angle), 0.0, Width);
                pa -= Math.Floor(pa / Circ) * Circ;
                lowest = Math.Min(lowest, RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, pa, pz) - bottom);
            }
            checkedCount++;
            worst = Math.Min(worst, lowest);
            if (lowest < 0.0) { exposed++; if (firstBad.Length == 0) firstBad = $" first at ({a:F1}, {z:F1}) size {r.Size:F1}, terrain {-lowest:F2} m below the buried base"; }
        }
        Detail($"i. buried bases: {checkedCount} outcrops (14 tiles plus the steep_eye disc) checked on the centre and 16 ring points at {row.BuriedFootprintRadius} x size; lowest margin of any point above the buried base {worst:F2} m (rule: at least 0 above base + {row.BuriedClearanceM} m); {exposed} exposed");
        Check(checkedCount >= 20, $"i. only {checkedCount} outcrops in the buried-base check");
        Check(exposed == 0, $"i. {exposed} of {checkedCount} outcrops have terrain below their buried base within the footprint;{firstBad}");
    }

    // ------------------------------------------------------------------ e. ground agreement

    // Tors on convex crests (A5 crest term) are exempt; Lead decision 2026-11-18. The crest term does not read rock exposure, so the A2 agreement rule (the 16 m grid's rock signal against the
    // terrain's 4 m rock) applies to roots where the grid's RockExposure is at least 0.3. The all-roots fraction and the crest-only roots are printed, not failed.
    private void GroundAgreement()
    {
        var roots = new List<ScatterRecord>();
        foreach (List<ScatterRecord> list in _outcropsByTile.Values) roots.AddRange(list);
        int good = 0, rocky = 0, rockyGood = 0;
        var hist = new int[10];
        var crestOnly = new List<(double Slope16, double Relief)>();
        foreach (ScatterRecord r in roots)
        {
            double a = r.AroundCm / 100.0, z = r.AcrossCm / 100.0;
            float rx = RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a + 2.0, z) - RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a - 2.0, z);
            float rz = RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a, z + 2.0) - RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a, z - 2.0);
            double slope = Math.Atan(Math.Sqrt((double)rx * rx + (double)rz * rz) / 4.0) * 180.0 / Math.PI;
            RingHabitatSample f = _grid.Sample(a, z);
            double rock = RingGroundSurfaceModel.RockExposure(slope, 4.0, f.Relief64, f.ForestWeight);
            hist[Math.Clamp((int)(rock * 10.0), 0, 9)]++;
            bool ok = rock >= 0.3;
            if (ok) good++;
            if (f.RockExposure >= 0.3) { rocky++; if (ok) rockyGood++; }
            else crestOnly.Add((f.SlopeDeg, f.Relief64));
        }
        double all = roots.Count > 0 ? (double)good / roots.Count : 0.0, fraction = rocky > 0 ? (double)rockyGood / rocky : 0.0;
        Detail($"e. ground agreement over {roots.Count} outcrop roots (14 tiles plus the steep_eye disc): all roots {good} ({100.0 * all:F1} %, printed only); roots with grid RockExposure >= 0.3: {rockyGood} of {rocky} have terrain rock exposure >= 0.3 on the 4 m baseline = {100.0 * fraction:F1} %; terrain rock exposure in 0.1 bins (all roots): {string.Join(" ", hist)}");
        Detail($"e. crest-only roots (grid RockExposure < 0.3): {crestOnly.Count}" + (crestOnly.Count > 0 ? $", mean 16 m slope {crestOnly.Average(c => c.Slope16):F1} deg, mean Relief64 {crestOnly.Average(c => c.Relief):F2}" : ""));
        // Lead decision 2026-11-19 (T5-10b): 0.75, not design A2's estimated 0.8. The deeper outcrop foot (-1.25) re-admits steep-face outcrops, where the 16 m grid and the
        // 4 m terrain baseline disagree more; measured 57 of 73 = 78.1 %, and with n of about 70 one standard error is about 5 points. A real regression (outcrops off rock) falls far below.
        Check(rocky >= 10 && fraction >= 0.75,$"e. only {rockyGood} of {rocky} outcrop roots with grid rock exposure >= 0.3 ({100.0 * fraction:F1} %) have terrain rock exposure >= 0.3");
    }

    // ------------------------------------------------------------------ g. summary

    private void Summary()
    {
        var lines = new List<string>();
        foreach (TileData t in _tiles)
        {
            int o = t.Records.Count(r => r.ClassId == Outcrop), b = t.Records.Count(r => r.ClassId == Boulder), c = t.Records.Count(r => r.ClassId == Cobble);
            double ha = t.Hectares;
            lines.Add($"  {t.Name,-22} outcrops {o,4} ({o / ha,6:F2}/ha)  boulders {b,5} ({b / ha,7:F1}/ha)  cobble clusters {c,5} ({c / ha,7:F1}/ha)   O {t.Result.OutcropMs,7:F2} ms  B {t.Result.BoulderMs,7:F2} ms  C {t.Result.CobbleMs,7:F2} ms");
        }
        Detail("g. per pose tile (counts, per hectare, stage ms; first build on a fresh sampler copy, this machine):\n" + string.Join("\n", lines));
    }

    // ------------------------------------------------------------------ h. plains

    private void Plains()
    {
        foreach (string name in new[] { "plains_eye", "lowland_eye" })
        {
            TileData t = T(name);
            int b = t.Records.Count(r => r.ClassId == Boulder);
            Detail($"h. {name}: {b} lone boulders = {b / t.Hectares:F2} per hectare");
            Check(b > 0, $"h. {name} has no boulders");
        }
    }

    // ------------------------------------------------------------------ debug plot

    private void Plot()
    {
        const int px = 480; // 240 m at 2 px per metre
        var image = Image.CreateEmpty(px * 3, px, false, Image.Format.Rgb8);
        var panels = new List<TileData> { T("steep_eye"), T("breaks_300m") };
        if (panels.All(p => p.Records.All(r => r.ClassId != Outcrop)) && _discTiles.Count > 0)
        {
            var best = _discTiles.OrderByDescending(d => _outcropsByTile[d.Key].Count).First();
            TileData extra = MakeTile("disc_most_outcrops", (long)Math.Round(best.A0 / _tileA), (long)Math.Round(best.Z0 / 240.0));
            extra.Trees = _ctx.BuildTreeSet(_root, extra.Key);
            using RingTerrainSampler copy = _root.CreateIndependentCopy();
            extra.Records = RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, extra.Key, extra.Trees, Array.Empty<ScatterRecord>(), 0, _table.BuildOrder.Count, 0xFFFF).Records;
            panels.Add(extra);
        }
        int panel = 0;
        foreach (TileData t in panels)
        {
            int ox = panel * px;
            _grid.PrepareWindow(t.A0 - 16.0, t.A1 + 16.0, t.Z0 - 16.0, t.Z1 + 16.0);
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    RingHabitatSample f = _grid.Sample(t.A0 + (x + 0.5) * 0.5, t.Z0 + (px - 1 - y + 0.5) * 0.5);
                    float g = 0.15f + 0.7f * f.RockExposure;
                    image.SetPixel(ox + x, y, new Color(g, g, g));
                }
            foreach (ScatterRecord r in t.Records)
            {
                double x = (r.AroundCm / 100.0 - t.A0) * 2.0, y = px - 1 - (r.AcrossCm / 100.0 - t.Z0) * 2.0;
                if (r.ClassId == Outcrop) Disc(image, ox, px, x, y, r.Size, new Color(1f, 0.55f, 0.05f), ring: true);
                else if (r.ClassId == Boulder) Disc(image, ox, px, x, y, Math.Max(1.0, r.Size), new Color(0.95f, 0.1f, 0.1f), ring: false);
                else Disc(image, ox, px, x, y, 1.0, new Color(1f, 0.95f, 0.1f), ring: false);
            }
            panel++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PngPath)!);
        Error err = image.SavePng(PngPath);
        Detail($"plot: steep_eye, breaks_300m and (when those have no outcrop) the disc tile with most outcrops, left to right, 1 px = 0.5 m, rock exposure grey, outcrops orange rings of their footprint, boulders red, cobbles yellow -> {PngPath} ({err})");
    }

    private static void Disc(Image image, int ox, int px, double cx, double cy, double radiusPx, Color c, bool ring)
    {
        int r = (int)Math.Ceiling(radiusPx);
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                double d = Math.Sqrt(dx * dx + dy * dy);
                bool on = ring ? Math.Abs(d - radiusPx) <= 1.0 : d <= radiusPx;
                int x = (int)Math.Round(cx) + dx, y = (int)Math.Round(cy) + dy;
                if (on && x >= 0 && x < px && y >= 0 && y < px) image.SetPixel(ox + x, y, c);
            }
    }
}
