using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using Ctx = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterBuildContext;
using Key = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTileKey;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-4a (docs/work-orders/terrain-vegetation/T6.md, design A7): the understory sapling row (Id 12, veg.sapling) in a vegetation.forest_structure world. Density at forest edges is at least three times
/// dense forest; nothing on site masks, water, slopes over 34 degrees or within 1.5 m of an accepted trunk; every mesh is an Understory-role variant and no canopy tree is a sapling; records are identical
/// across repeated builds, reversed tile order and 1 vs 4 workers; a rocks-only world is unchanged by the mask views. T6-6 extends it with the dead-wood rows (snag 10, fallen tree 13, stump 14): dead wood only where its communities
/// have membership (snags also through their constant term), snag density rising with stand age, no fallen tree within 0.5 x its length of a canopy trunk or on a site mask, every log's two ends within 0.3 m of the exact ground (the tilt fit),
/// resident counts within 300 m of forest_interior_eye under the caps, and the rock records unchanged in a world that has both bits. T6-7 adds the shrub row (Id 11, veg.shrub): density at ForestEdge at least three times Dense, nothing on masks, water, slopes over 38 degrees or within 1.0 m of a trunk, Shrub-role meshes only, the look following the overrides (wet meadow broadleaf, dry meadow and fell pine scrub), the 3,000 cap holding within 150 m at forest_interior_eye and forest_edge_eye, and the eye-level free path (rays at 1.7 m through trees, saplings and shrubs) of 25 to 70 m in the tiles whose dominant community is ClosedBroadleaf. Prints UNDERSTORY_PLACEMENT=PASS. Headless: tools/run_tests.ps1 -Tests understory_placement_test.
/// </summary>
public partial class UnderstoryPlacementTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const string DressingFixture = "res://tools/fixtures/landscape_poses_dressing_s1337.json";
    private const ushort Sapling = 12, Snag = 10, Fallen = 13, Stump = 14, Shrub = 11, StandFill = 15;
    private static readonly long Cells = (long)Math.Ceiling(Circ / 30.0);
    private static readonly double AroundCell = Circ / Math.Ceiling(Circ / 30.0);
    private static readonly string[] ForestPoses = { "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye" };

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("UNDERSTORY_PLACEMENT_DETAIL " + text);

    private sealed class Tile
    {
        public string Pose = "";
        public Key Key;
        public double A0, Z0;
        public List<ScatterRecord> Records = new();
        public double BuildMs, StageMs;
    }

    private RingTerrainSampler _root = null!, _probe = null!;
    private RingHabitatFieldGrid _grid = null!;
    private Ctx _ctx = null!;
    private double _tileA;
    private readonly List<Tile> _tiles = new();

    public int Run()
    {
        try
        {
            Setup();
            Build();
            Density();
            Exclusions();
            Roles();
            Shrubs();
            EyeLevel();
            DeadWood();
            SteepLogs();
            Determinism();
            RocksUnchanged();
            Report();
        }
        catch (Exception e) { Console.Error.WriteLine("UNDERSTORY_PLACEMENT=FAIL exception " + e); return 1; }
        finally { _probe?.Dispose(); _root?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("UNDERSTORY_PLACEMENT=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("UNDERSTORY_PLACEMENT=PASS");
        return 0;
    }

    private static IEnumerable<ScatterRecord> Saplings(Tile t) => t.Records.Where(r => r.ClassId == Sapling);
    private static IEnumerable<ScatterRecord> ShrubRecords(Tile t) => t.Records.Where(r => r.ClassId == Shrub);

    private static long Mod(long v, long n) { long r = v % n; return r < 0 ? r + n : r; }
    private static double WrapDelta(double d) => d > Circ * .5 ? d - Circ : d < -Circ * .5 ? d + Circ : d;

    private void Setup()
    {
        LandscapeWorld.Spec spec = LandscapeWorld.For(1, new[] { "vegetation.forest_structure" });
        _root = spec.CreateSampler();
        Check((_root.WorldFeatureMask & (1 << 6)) != 0 && (_root.WorldFeatureMask & (1 << 4)) == 0, "the test world must have bit 6 and not bit 4");
        Check(RingHabitatFieldGrid.IsEnabled(_root.WorldFeatureMask), "the grid is not enabled by bit 6");
        _ctx = Ctx.ForReview(LandscapeWorld.Seed, Circ, Width, RingScatterClassTable.Production, null, _root.WorldFeatureMask);
        Check(_ctx.Table.Rows.Select(r => (int)r.Id).OrderBy(i => i).SequenceEqual(new[] { 10, 11, 12, 13, 14, 15 }), "the bit 6 table is not the snag, shrub, sapling, fallen tree, stump and stand filler rows");
        _tileA = _ctx.TileCells * _ctx.AroundCellMeters;
        _probe = _root.CreateIndependentCopy();
        _grid = new RingHabitatFieldGrid(_probe, _ctx.Edits, LandscapeWorld.Seed, Circ, Width);
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        int lastZ = (Width - 1) / 240;
        foreach (string name in ForestPoses.Append("plains_eye"))
        {
            LandscapePose p = t0.Poses.First(x => x.Name == name);
            long ta = (long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            int span = name == "plains_eye" ? 0 : 1;
            for (int dz = -span; dz <= span; dz++)
                for (int da = -span; da <= span; da++)
                {
                    long z = tz + dz;
                    if (z < 0 || z > lastZ) continue;
                    long a = Mod(ta + da, _ctx.AroundTileCount);
                    _tiles.Add(new Tile { Pose = name, Key = new Key(a, z), A0 = a * _tileA, Z0 = z * 240.0 });
                }
        }
    }

    private List<ScatterRecord> BuildTile(RingTerrainSampler sampler, Key key, out double totalMs, out double stageMs)
    {
        var sw = Stopwatch.StartNew();
        var trees = _ctx.BuildTreeSet(sampler, key);
        double treeMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        using RingTerrainSampler copy = sampler.CreateIndependentCopy();
        var result = RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, key, trees, Array.Empty<ScatterRecord>(), 0, _ctx.Table.BuildOrder.Count, 0xFFFF);
        totalMs = sw.Elapsed.TotalMilliseconds; stageMs = result.BoulderMs;
        _ = treeMs;
        return result.Records;
    }

    private void Build()
    {
        BuildTile(_root, _tiles[0].Key, out _, out _); // warm-up
        foreach (Tile t in _tiles) t.Records = BuildTile(_root, t.Key, out t.BuildMs, out t.StageMs);
        Detail($"built {_tiles.Count} tiles, {_tiles.Sum(t => Saplings(t).Count())} sapling records, {_tiles.Sum(t => t.Records.Count(r => r.ClassId == Snag))} snags, {_tiles.Sum(t => t.Records.Count(r => r.ClassId == Fallen))} fallen trees, {_tiles.Sum(t => t.Records.Count(r => r.ClassId == Stump))} stumps"
            );
    }

    // ------------------------------------------------------------------ fields at a point

    private (int Bin, bool Usable) BinAt(double a, double z)
    {
        RingHabitatSample f = _grid.Sample(a, z);
        bool usable = (f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) == 0 && f.SlopeDeg < 28f;
        var regional = new ForestRegionSample(f.Habitat, f.Canopy, 0f, f.Clearing, f.MapleWeight, f.FirWeight, f.PineWeight);
        ForestStructureSampler.SpeciesWeights(f.Moisture, f.Temperature, f.WetnessTotal, f.Relief64, f.RockExposure, f.SlopeDeg, in regional, out _, out float fir, out float pine);
        RingCommunityMemberships m = RingPlantCommunities.Memberships(f.Canopy, f.Clearing, fir + pine, f.WetnessTotal, f.Relief64, f.RockExposure, f.Moisture, f.Scree);
        float[] v = { m.ClosedBroadleaf + m.ConiferStand, m.ForestEdge, m.OpenWoodland, m.Clearing, MathF.Max(MathF.Max(m.DryMeadow, m.WetMeadow), MathF.Max(m.RockyFell, m.Grassland)) };
        int best = 0;
        for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return (best, usable);
    }

    private static readonly string[] BinNames = { "Dense", "ForestEdge", "OpenWoodland", "Clearing", "Other" };

    private void Density()
    {
        var area = new double[5]; var count = new int[5]; var shrubCount = new int[5];
        var deadCount = new Dictionary<int, int[]> { [Snag] = new int[5], [Fallen] = new int[5], [Stump] = new int[5] };
        foreach (Tile t in _tiles.Where(t => t.Pose != "plains_eye"))
        {
            for (double a = t.A0 + 4.0; a < t.A0 + _tileA; a += 8.0)
                for (double z = t.Z0 + 4.0; z < t.Z0 + 240.0; z += 8.0)
                {
                    if (z < 4.0 || z > Width - 4.0) continue;
                    (int bin, bool usable) = BinAt(a, z);
                    if (usable) area[bin] += 64.0;
                }
            foreach (ScatterRecord r in Saplings(t))
            {
                (int bin, bool usable) = BinAt(r.AroundCm * .01, r.AcrossCm * .01);
                if (usable) count[bin]++;
            }
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId != Sapling && r.ClassId != Shrub && r.ClassId != StandFill))
            {
                (int bin, bool usable) = BinAt(r.AroundCm * .01, r.AcrossCm * .01);
                if (usable) deadCount[r.ClassId][bin]++;
            }
            foreach (ScatterRecord r in ShrubRecords(t))
            {
                (int bin, bool usable) = BinAt(r.AroundCm * .01, r.AcrossCm * .01);
                if (usable) shrubCount[bin]++;
            }
        }
        foreach (ushort id in new[] { Snag, Fallen, Stump })
            Detail($"dead wood row {id} density per ha by dominant membership: " + string.Join(", ", Enumerable.Range(0, 5).Select(i => $"{BinNames[i]} {(area[i] > 0 ? deadCount[id][i] / (area[i] / 10000.0) : 0.0):F2} ({deadCount[id][i]})")));
        var density = new double[5];
        for (int i = 0; i < 5; i++) density[i] = area[i] > 0 ? count[i] / (area[i] / 10000.0) : 0.0;
        Detail("density per ha by dominant membership: " + string.Join(", ", Enumerable.Range(0, 5).Select(i => $"{BinNames[i]} {density[i]:F1} ({area[i] / 10000.0:F1} ha, {count[i]} records)")));
        Check(area[0] >= 2000.0 && area[1] >= 2000.0, $"too little area to compare (Dense {area[0] / 10000.0:F2} ha, ForestEdge {area[1] / 10000.0:F2} ha)");
        Check(density[0] > 0.0 && density[1] >= 3.0 * density[0], $"sapling density at ForestEdge {density[1]:F1}/ha is not 3x Dense {density[0]:F1}/ha");
        var shrubs = new double[5];
        for (int i = 0; i < 5; i++) shrubs[i] = area[i] > 0 ? shrubCount[i] / (area[i] / 10000.0) : 0.0;
        Detail("shrub density per ha by dominant membership: " + string.Join(", ", Enumerable.Range(0, 5).Select(i => $"{BinNames[i]} {shrubs[i]:F1} ({shrubCount[i]} records)")) + $"; ForestEdge / Dense = {(shrubs[0] > 0 ? shrubs[1] / shrubs[0] : 0.0):F2}");
        Check(shrubs[0] > 0.0 && shrubs[1] >= 3.0 * shrubs[0], $"shrub density at ForestEdge {shrubs[1]:F1}/ha is not 3x Dense {shrubs[0]:F1}/ha");
    }

    // ------------------------------------------------------------------ exclusions and roles

    private List<(double A, double Z, int Variant)> TreesAround(Tile t, out bool anySapling)
    {
        var trees = new List<(double, double, int)>();
        anySapling = false;
        int[] saplings = { 0, 1, 2, 14, 24, 25, 26 };
        var seen = new HashSet<Key>();
        for (int dz = -1; dz <= 1; dz++)
            for (int da = -1; da <= 1; da++)
            {
                long z = t.Key.Across + dz; if (z < 0) continue;
                var k = new Key(Mod(t.Key.Around + da, _ctx.AroundTileCount), z);
                if (!seen.Add(k)) continue;
                foreach (var rec in RingBiomeDecorationLayer.BuildImportedForestReviewTile(_root, k.Around, k.Across, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width))
                {
                    trees.Add((rec.Around, rec.Across, rec.Variant));
                    if (saplings.Contains(rec.Variant)) anySapling = true;
                }
            }
        return trees;
    }

    private void Exclusions()
    {
        int blocked = 0, water = 0, steep = 0, nearTrunk = 0, total = 0;
        double minTrunk = double.MaxValue;
        bool anySaplingTree = false;
        foreach (Tile t in _tiles)
        {
            if (!Saplings(t).Any()) continue;
            var trees = TreesAround(t, out bool sap);
            anySaplingTree |= sap;
            foreach (ScatterRecord r in Saplings(t))
            {
                total++;
                double a = r.AroundCm * .01, z = r.AcrossCm * .01;
                RingHabitatSample f = _grid.Sample(a, z);
                if ((f.Flags & RingHabitatFlags.SiteBlocked) != 0) blocked++;
                if ((f.Flags & RingHabitatFlags.Water) != 0) water++;
                if (f.SlopeDeg > 34.0001f) steep++;
                foreach ((double ta, double tz, int _) in trees)
                {
                    double da = WrapDelta(ta - a), dz = tz - z, d = Math.Sqrt(da * da + dz * dz);
                    if (d < minTrunk) minTrunk = d;
                    if (d < 1.5 - 1e-6) { nearTrunk++; break; }
                }
            }
        }
        Check(total > 0, "no sapling records at all");
        Check(blocked == 0, $"{blocked} records on a site mask");
        Check(water == 0, $"{water} records in water");
        Check(steep == 0, $"{steep} records on slopes over 34 degrees");
        Check(nearTrunk == 0, $"{nearTrunk} records within 1.5 m of an accepted tree root");
        Check(!anySaplingTree, "a canopy tree record is a sapling variant");
        Detail($"exclusions over {total} records: site {blocked}, water {water}, steep {steep}, nearest trunk {minTrunk:F2} m (limit 1.5)");
    }

    private void Roles()
    {
        int bad = 0, wrongClass = 0;
        var species = new Dictionary<TreeSpecies, int>();
        foreach (ScatterRecord r in _tiles.SelectMany(Saplings))
        {
            if (r.ClassId != Sapling) wrongClass++;
            ImportedTreeSourceRow row = ImportedTreeSourceTable.Row(r.Mesh);
            if (row.Role != TreePlacementRole.Understory || row.Form != TreeFormKind.Tree) bad++;
            species[row.Species] = species.GetValueOrDefault(row.Species) + 1;
        }
        Check(bad == 0, $"{bad} records use a mesh that is not an Understory-role tree variant");
        Check(wrongClass == 0, $"{wrongClass} records are not class 12");
        Detail("species mix: " + string.Join(", ", species.Select(p => $"{p.Key} {p.Value}")));
    }

    // ------------------------------------------------------------------ shrubs (T6-7)

    private void Shrubs()
    {
        int total = 0, blocked = 0, water = 0, steep = 0, nearTrunk = 0, wrongRole = 0, wrongLook = 0, overridden = 0;
        double minTrunk = double.MaxValue;
        var looks = new Dictionary<TreeSpecies, int>();
        var overrides = new Dictionary<string, int[]>();
        foreach (Tile t in _tiles)
        {
            if (!ShrubRecords(t).Any()) continue;
            var trees = TreesAround(t, out _);
            foreach (ScatterRecord r in ShrubRecords(t))
            {
                total++;
                double a = r.AroundCm * .01, z = r.AcrossCm * .01;
                RingHabitatSample f = _grid.Sample(a, z);
                if ((f.Flags & RingHabitatFlags.SiteBlocked) != 0) blocked++;
                if ((f.Flags & RingHabitatFlags.Water) != 0) water++;
                if (f.SlopeDeg > 38.0001f) steep++;
                foreach ((double ta, double tz, int _) in trees)
                {
                    double da = WrapDelta(ta - a), dz = tz - z, d = Math.Sqrt(da * da + dz * dz);
                    if (d < minTrunk) minTrunk = d;
                    if (d < 1.0 - 1e-6) { nearTrunk++; break; }
                }
                ImportedTreeSourceRow row = ImportedTreeSourceTable.Row(r.Mesh);
                if (row.Role != TreePlacementRole.Shrub || row.Form != TreeFormKind.Shrub || r.Mesh < 43 || r.Mesh > 54) wrongRole++;
                looks[row.Species] = looks.GetValueOrDefault(row.Species) + 1;
                // The look follows the overrides: WetMeadow dominant -> broadleaf (maple); DryMeadow or RockyFell dominant -> pine scrub.
                RingCommunityMemberships m = MembershipsAt(a, z);
                int best = 0;
                for (int i = 1; i < RingCommunityMemberships.Count; i++) if (m[i] > m[best]) best = i;
                string name = ((RingPlantCommunity)best).ToString();
                if (best == (int)RingPlantCommunity.WetMeadow || best == (int)RingPlantCommunity.DryMeadow || best == (int)RingPlantCommunity.RockyFell)
                {
                    overridden++;
                    TreeSpecies expected = best == (int)RingPlantCommunity.WetMeadow ? TreeSpecies.Maple : TreeSpecies.Pine;
                    if (row.Species != expected) wrongLook++;
                    if (!overrides.TryGetValue(name, out int[]? c)) overrides[name] = c = new int[2];
                    c[row.Species == expected ? 0 : 1]++;
                }
            }
        }
        Check(total > 0, "no shrub records at all");
        Check(blocked == 0, $"{blocked} shrubs on a site mask");
        Check(water == 0, $"{water} shrubs in water");
        Check(steep == 0, $"{steep} shrubs on slopes over 38 degrees");
        Check(nearTrunk == 0, $"{nearTrunk} shrubs within 1.0 m of an accepted tree root");
        Check(wrongRole == 0, $"{wrongRole} shrub records use a mesh that is not a Shrub-role form 43-54");
        Check(overridden > 0, "no shrub stands in a wet meadow, dry meadow or rocky fell at the test tiles, so the look overrides are not exercised");
        Check(wrongLook == 0, $"{wrongLook} of {overridden} shrubs in a wet meadow (broadleaf), dry meadow or rocky fell (pine scrub) have the wrong look");
        Detail($"shrubs: {total} records; site {blocked}, water {water}, steep {steep}, nearest trunk {minTrunk:F2} m (limit 1.0); looks " + string.Join(", ", looks.Select(p => $"{p.Key} {p.Value}"))
            + $"; override communities (right/wrong look): " + string.Join(", ", overrides.Select(p => $"{p.Key} {p.Value[0]}/{p.Value[1]}")));
        // Resident counts within 150 m of the two forest poses (the layer cap of the shrub slot is 3,000 and so is the row's).
        var poses = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        int cap = _ctx.Table.Find(Shrub)!.ResidentCap;
        foreach (string name in new[] { "forest_interior_eye", "forest_edge_eye" })
        {
            LandscapePose pose = poses.Poses.First(x => x.Name == name);
            long ta0 = (long)Math.Floor(Math.Floor(pose.Around / AroundCell) / 8.0), tz0 = (long)Math.Floor(pose.Across / 240.0);
            int lastZ = (Width - 1) / 240, within = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int da = -1; da <= 1; da++)
                {
                    long zk = tz0 + dz; if (zk < 0 || zk > lastZ) continue;
                    var key = new Key(Mod(ta0 + da, _ctx.AroundTileCount), zk);
                    List<ScatterRecord> records = _tiles.FirstOrDefault(t => t.Key == key)?.Records ?? BuildTile(_root, key, out _, out _);
                    foreach (ScatterRecord r in records.Where(r => r.ClassId == Shrub))
                    {
                        double ddA = WrapDelta(r.AroundCm * .01 - pose.Around), ddZ = r.AcrossCm * .01 - pose.Across;
                        if (Math.Sqrt(ddA * ddA + ddZ * ddZ) <= 150.0) within++;
                    }
                }
            Detail($"{name}: {within} shrubs within 150 m (cap {cap})");
            Check(within <= cap, $"{name}: {within} shrubs within 150 m exceed the cap {cap}");
        }
    }

    // ------------------------------------------------------------------ eye-level free path (T6-7, design A7)

    private int DominantCommunity(Tile t)
    {
        var sum = new double[RingCommunityMemberships.Count];
        for (double a = t.A0 + 4.0; a < t.A0 + _tileA; a += 8.0)
            for (double z = t.Z0 + 4.0; z < t.Z0 + 240.0; z += 8.0)
            {
                if (z < 4.0 || z > Width - 4.0) continue;
                RingCommunityMemberships m = MembershipsAt(a, z);
                for (int i = 0; i < sum.Length; i++) sum[i] += m[i];
            }
        int best = 0;
        for (int i = 1; i < sum.Length; i++) if (sum[i] > sum[best]) best = i;
        return best;
    }

    /// <summary>Rays at 1.7 m through the tiles' trees (0.6 m wide, as the existing estimate), maple saplings (2.2 m), pine and fir saplings (0) and shrubs taller than 1.7 m (the look's width x (1 - 0.35)): the mean free path in the tiles whose dominant community is ClosedBroadleaf is 25 to 70 m (A7: 44 m by the estimate).</summary>
    private void EyeLevel()
    {
        var perPose = new List<string>();
        int closedTiles = 0;
        foreach (string pose in ForestPoses.Append("plains_eye"))
        {
            var means = new List<double>();
            foreach (Tile t in _tiles.Where(t => t.Pose == pose))
            {
                if (DominantCommunity(t) != (int)RingPlantCommunity.ClosedBroadleaf) continue;
                var trees = TreesAround(t, out _);
                var obstacles = new List<(double A, double Z, double Width)>();
                foreach (var tree in trees) obstacles.Add((tree.A, tree.Z, 0.6));
                foreach (Tile other in _tiles.Where(o => o.Pose == pose))
                {
                    foreach (ScatterRecord r in Saplings(other))
                        obstacles.Add((r.AroundCm * .01, r.AcrossCm * .01, ImportedTreeSourceTable.Row(r.Mesh).Species == TreeSpecies.Maple ? 2.2 : 0.0));
                    foreach (ScatterRecord r in ShrubRecords(other))
                    {
                        if (r.Size <= 1.7f) continue;
                        double lookWidth = ImportedTreeSourceTable.Row(r.Mesh).Species switch { TreeSpecies.Maple => 1.1, TreeSpecies.Pine => 1.4, _ => 1.2 };
                        obstacles.Add((r.AroundCm * .01, r.AcrossCm * .01, lookWidth * r.Size * (1.0 - 0.35)));
                    }
                }
                means.Add(FreePath(t, obstacles));
            }
            closedTiles += means.Count;
            if (means.Count == 0) { perPose.Add($"{pose}: no ClosedBroadleaf tile"); continue; }
            double mean = means.Average();
            perPose.Add($"{pose}: {mean:F1} m over {means.Count} ClosedBroadleaf tile(s) ({string.Join(", ", means.Select(m => m.ToString("F1")))})");
            Check(mean >= 25.0 && mean <= 70.0, $"{pose}: the eye-level free path {mean:F1} m in ClosedBroadleaf tiles is outside 25-70 m");
        }
        Detail("eye-level free path (trees + saplings + shrubs): " + string.Join("; ", perPose));
        Check(closedTiles > 0, "no tile of the test poses has ClosedBroadleaf as its dominant community, so the eye-level free path is not checked");
    }

    // ------------------------------------------------------------------ dead wood (T6-6)

    private static readonly (int Id, string Name, Func<RingCommunityMemberships, float> Share)[] DeadWoodCommunities =
    {
        (Snag, "snag", m => m.ClosedBroadleaf + m.ConiferStand + m.OpenWoodland),
        (Fallen, "fallen", m => m.ClosedBroadleaf + m.ConiferStand + m.ForestEdge),
        (Stump, "stump", m => m.ClosedBroadleaf + m.ConiferStand + m.Clearing + m.ForestEdge),
    };

    private RingCommunityMemberships MembershipsAt(double a, double z)
    {
        RingHabitatSample f = _grid.Sample(a, z);
        var regional = new ForestRegionSample(f.Habitat, f.Canopy, 0f, f.Clearing, f.MapleWeight, f.FirWeight, f.PineWeight);
        ForestStructureSampler.SpeciesWeights(f.Moisture, f.Temperature, f.WetnessTotal, f.Relief64, f.RockExposure, f.SlopeDeg, in regional, out _, out float fir, out float pine);
        return RingPlantCommunities.Memberships(f.Canopy, f.Clearing, fir + pine, f.WetnessTotal, f.Relief64, f.RockExposure, f.Moisture, f.Scree);
    }

    private double GroundAt(double a, double z) => RingBiomeDecorationLayer.PresentationSurfaceHeight(_ctx.Edits, _probe, a - Math.Floor(a / Circ) * Circ, z);

    private static double LogLength(in ScatterRecord r, IReadOnlyList<ImportedTreeVariant> catalog)
        => RingBiomeDecorationLayer.LogLengthFraction * r.Size * catalog[ImportedTreeSourceTable.Row(r.Mesh).SourceBaseVariant].HeightMeters;

    private void DeadWood()
    {
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        RingScatterClassTable table = _ctx.Table;
        // (1) Roles and forms: each dead-wood row places only its own form, with the DeadWood role.
        var forms = new Dictionary<ushort, TreeFormKind> { [Snag] = TreeFormKind.Snag, [Fallen] = TreeFormKind.FallenTree, [Stump] = TreeFormKind.Stump };
        int wrongForm = 0, outside = 0, snagConstant = 0, snagTotal = 0, total = 0;
        foreach (ScatterRecord r in _tiles.SelectMany(t => t.Records).Where(r => r.ClassId != Sapling && r.ClassId != Shrub && r.ClassId != StandFill))
        {
            total++;
            ImportedTreeSourceRow row = ImportedTreeSourceTable.Row(r.Mesh);
            if (row.Role != TreePlacementRole.DeadWood || row.Form != forms[r.ClassId]) wrongForm++;
            // (2) Only where the row's communities have membership above 0.05; the snag row's constant term (0.05) also places a few anywhere.
            double a = r.AroundCm * .01, z = r.AcrossCm * .01;
            float share = DeadWoodCommunities.First(c => c.Id == r.ClassId).Share(MembershipsAt(a, z));
            if (share > 0.05f) continue;
            if (r.ClassId == Snag) { snagConstant++; } else outside++;
        }
        foreach (ScatterRecord r in _tiles.SelectMany(t => t.Records).Where(r => r.ClassId == Snag)) snagTotal++;
        Check(total > 0, "no dead-wood records at all");
        Check(wrongForm == 0, $"{wrongForm} dead-wood records use a mesh that is not their row's form with the DeadWood role");
        Check(outside == 0, $"{outside} fallen trees or stumps stand where their communities have membership of at most 0.05");
        Check(snagTotal == 0 || snagConstant <= 0.25 * snagTotal, $"{snagConstant} of {snagTotal} snags stand outside Dense and OpenWoodland (the constant term allows few)");
        Detail($"dead wood: {total} records; fallen/stump outside their communities {outside}; snags outside Dense/OpenWoodland {snagConstant} of {snagTotal}");

        // (3) Snag density rises with stand age: usable area and snag records binned into five stand-age quintiles (0.2 wide on the effective age).
        ForestStructureSampler structure = ForestStructureSampler.For(_probe);
        var area = new double[5]; var snags = new int[5];
        int Bin(double a, double z)
        {
            RingHabitatSample f = _grid.Sample(a, z);
            float age = ForestStructureSampler.EffectiveAge(structure.ContrastedStandAge((long)Math.Floor(a), (int)Math.Floor(z)), f.Canopy, f.Clearing, f.Temperature);
            return Math.Clamp((int)(age * 5f), 0, 4);
        }
        foreach (Tile t in _tiles.Where(t => t.Pose != "plains_eye"))
        {
            for (double a = t.A0 + 4.0; a < t.A0 + _tileA; a += 8.0)
                for (double z = t.Z0 + 4.0; z < t.Z0 + 240.0; z += 8.0)
                {
                    if (z < 4.0 || z > Width - 4.0) continue;
                    RingHabitatSample f = _grid.Sample(a, z);
                    if ((f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) != 0 || f.SlopeDeg >= 28f) continue;
                    area[Bin(a, z)] += 64.0;
                }
            foreach (ScatterRecord r in t.Records.Where(r => r.ClassId == Snag)) snags[Bin(r.AroundCm * .01, r.AcrossCm * .01)]++;
        }
        double[] perHa = Enumerable.Range(0, 5).Select(i => area[i] > 0 ? snags[i] / (area[i] / 10000.0) : 0.0).ToArray();
        Detail("snags per ha by stand-age quintile: " + string.Join(", ", Enumerable.Range(0, 5).Select(i => $"Q{i + 1} {perHa[i]:F2} ({snags[i]} in {area[i] / 10000.0:F1} ha)")));
        Check(area[0] + area[1] >= 20000.0 && area[3] + area[4] >= 20000.0, $"too little area in the low and high stand-age quintiles ({(area[0] + area[1]) / 10000.0:F1} and {(area[3] + area[4]) / 10000.0:F1} ha)");
        double low = (snags[0] + snags[1]) / Math.Max(1.0, (area[0] + area[1]) / 10000.0), high = (snags[3] + snags[4]) / Math.Max(1.0, (area[3] + area[4]) / 10000.0);
        Check(perHa[4] > perHa[0] && high > 1.5 * low, $"snag density does not rise with stand age (Q1 {perHa[0]:F2}, Q5 {perHa[4]:F2}; lower two {low:F2}, upper two {high:F2} per ha)");

        // (4) Fallen trees: no accepted canopy trunk within 0.5 x length of the midpoint (so none crosses a trunk), nothing on a site mask or water at the midpoint or either end, rims respected.
        // (5) Both ends within 0.3 m of the exact ground: the chord's end heights (with the record's embed along its up) against PresentationSurfaceHeight at the ends.
        RingScatterClass fallenRow = table.Find(Fallen)!;
        int logs = 0, nearTrunk = 0, masked = 0, badEnds = 0, steepLogs = 0;
        double worstEnd = 0.0, minTrunkRatio = double.MaxValue, worstSlope = 0.0, worstSteepEnd = 0.0, maxLength = 0.0;
        foreach (Tile t in _tiles)
        {
            List<ScatterRecord> mine = t.Records.Where(r => r.ClassId == Fallen).ToList();
            if (mine.Count == 0) continue;
            var trees = TreesAround(t, out _);
            foreach (ScatterRecord r in mine)
            {
                logs++;
                double a = r.AroundCm * .01, z = r.AcrossCm * .01, length = LogLength(r, catalog);
                maxLength = Math.Max(maxLength, length);
                foreach ((double ta, double tz, int _) in trees)
                {
                    double da = WrapDelta(ta - a), dz = tz - z, d = Math.Sqrt(da * da + dz * dz);
                    minTrunkRatio = Math.Min(minTrunkRatio, d / (0.5 * length));
                    if (d < 0.5 * length - 1e-6) { nearTrunk++; break; }
                }
                var (e0, e1) = ScatterLogFrame.Ends(r, length);
                foreach (var e in new[] { (a, z), (e0.Around, e0.Across), (e1.Around, e1.Across) })
                {
                    RingHabitatSample f = _grid.Sample(e.Item1, e.Item2);
                    if ((f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) != 0 || e.Item2 < 4.0 || e.Item2 > Width - 4.0) { masked++; break; }
                }
                (double ux, double uy, double uz) = ScatterRecord.DecodeUpOct(r.UpOct);
                double embed = fallenRow.EmbedMin * r.Size * catalog[ImportedTreeSourceTable.Row(r.Mesh).SourceBaseVariant].HeightMeters; // metres along up, downwards
                double slope = Math.Atan2(e1.Height - e0.Height, Math.Sqrt((e1.Around - e0.Around) * (e1.Around - e0.Around) + (e1.Across - e0.Across) * (e1.Across - e0.Across))) * 180.0 / Math.PI;
                double end0 = Math.Abs(e0.Height - embed * uy - GroundAt(e0.Around, e0.Across)), end1 = Math.Abs(e1.Height - embed * uy - GroundAt(e1.Around, e1.Across));
                double worst = Math.Max(end0, end1);
                worstEnd = Math.Max(worstEnd, worst);
                worstSlope = Math.Max(worstSlope, Math.Abs(slope));
                if (Math.Abs(slope) > 20.0) { steepLogs++; worstSteepEnd = Math.Max(worstSteepEnd, worst); }
                if (worst > 0.3) badEnds++;
            }
        }
        Check(logs > 0, "no fallen trees at the test tiles");
        Check(nearTrunk == 0, $"{nearTrunk} fallen trees within 0.5 x length of a canopy trunk");
        Check(masked == 0, $"{masked} fallen trees with the midpoint or an end on a site mask, water or outside the rims");
        Check(badEnds == 0, $"{badEnds} of {logs} fallen trees have an end more than 0.3 m from the exact ground (worst {worstEnd:F2} m)");
        Detail($"fallen trees: {logs}; longest {maxLength:F1} m; nearest trunk {minTrunkRatio:F2} x half length (limit 1.00); worst end error {worstEnd:F3} m (limit 0.30); steepest log {worstSlope:F1} deg, {steepLogs} logs over 20 deg (worst end {worstSteepEnd:F3} m)");

        // (6) Resident counts within 300 m of forest_interior_eye under the caps (the render slot holds 400 + 200 + 200; a row's resident cap is its own).
        LandscapePose interior = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath)).Poses.First(x => x.Name == "forest_interior_eye");
        long ta0 = (long)Math.Floor(Math.Floor(interior.Around / AroundCell) / 8.0), tz0 = (long)Math.Floor(interior.Across / 240.0);
        var counts = new Dictionary<ushort, int> { [Snag] = 0, [Fallen] = 0, [Stump] = 0 };
        var radius = new Dictionary<ushort, double> { [Snag] = 300.0, [Fallen] = table.Find(Fallen)!.ReachM, [Stump] = table.Find(Stump)!.ReachM };
        int lastZ = (Width - 1) / 240;
        for (int dz = -2; dz <= 2; dz++)
            for (int da = -2; da <= 2; da++)
            {
                long zk = tz0 + dz; if (zk < 0 || zk > lastZ) continue;
                var key = new Key(Mod(ta0 + da, _ctx.AroundTileCount), zk);
                List<ScatterRecord> records = _tiles.FirstOrDefault(t => t.Key == key)?.Records ?? BuildTile(_root, key, out _, out _);
                foreach (ScatterRecord r in records.Where(r => radius.ContainsKey(r.ClassId)))
                {
                    double ddA = WrapDelta(r.AroundCm * .01 - interior.Around), ddZ = r.AcrossCm * .01 - interior.Across;
                    if (Math.Sqrt(ddA * ddA + ddZ * ddZ) <= radius[r.ClassId]) counts[r.ClassId]++;
                }
            }
        Detail($"forest_interior_eye resident counts: snags {counts[Snag]} within 300 m (cap 200), fallen trees {counts[Fallen]} within {radius[Fallen]:F0} m (cap 400), stumps {counts[Stump]} within {radius[Stump]:F0} m (cap 200), dead wood total {counts.Values.Sum()} (slot cap 800)");
        Check(counts[Snag] <= table.Find(Snag)!.ResidentCap && counts[Fallen] <= table.Find(Fallen)!.ResidentCap && counts[Stump] <= table.Find(Stump)!.ResidentCap && counts.Values.Sum() <= 800, "dead-wood resident counts at forest_interior_eye exceed the caps");
    }

    /// <summary>The slope-laid fit on rough ground: the tiles around the dressing poses (steep, cliff, breaks, lowland) are scanned for fallen trees; every log's two ends stay within 0.3 m of the exact ground whatever its slope (escalation: logs over 20 degrees).</summary>
    private void SteepLogs()
    {
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        RingScatterClass fallenRow = _ctx.Table.Find(Fallen)!;
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        int lastZ = (Width - 1) / 240, logs = 0, bad = 0, over15 = 0, over20 = 0;
        double worst = 0.0, worst15 = 0.0, steepest = 0.0;
        var seen = new HashSet<Key>();
        foreach (string name in new[] { "steep_eye", "cliff_eye", "breaks_300m", "lowland_eye", "mountain_2km" })
        {
            LandscapePose p = dressing.Poses.First(x => x.Name == name);
            long ta = (long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            for (int dz = -2; dz <= 2; dz++)
                for (int da = -2; da <= 2; da++)
                {
                    long zk = tz + dz; if (zk < 0 || zk > lastZ) continue;
                    var key = new Key(Mod(ta + da, _ctx.AroundTileCount), zk);
                    if (!seen.Add(key)) continue;
                    foreach (ScatterRecord r in BuildTile(_root, key, out _, out _).Where(r => r.ClassId == Fallen))
                    {
                        logs++;
                        double length = LogLength(r, catalog);
                        var (e0, e1) = ScatterLogFrame.Ends(r, length);
                        (double ux, double uy, double uz) = ScatterRecord.DecodeUpOct(r.UpOct);
                        double embed = fallenRow.EmbedMin * r.Size * catalog[ImportedTreeSourceTable.Row(r.Mesh).SourceBaseVariant].HeightMeters;
                        double err = Math.Max(Math.Abs(e0.Height - embed * uy - GroundAt(e0.Around, e0.Across)), Math.Abs(e1.Height - embed * uy - GroundAt(e1.Around, e1.Across)));
                        double slope = Math.Abs(Math.Atan2(e1.Height - e0.Height, length) * 180.0 / Math.PI);
                        worst = Math.Max(worst, err); steepest = Math.Max(steepest, slope);
                        if (slope > 15.0) { over15++; worst15 = Math.Max(worst15, err); }
                        if (slope > 20.0) over20++;
                        if (err > 0.3) bad++;
                    }
                }
        }
        Detail($"fallen trees on rough ground: {logs} logs in {seen.Count} tiles, steepest {steepest:F1} deg, {over15} over 15 deg (worst end {worst15:F3} m), {over20} over 20 deg, worst end error {worst:F3} m");
        Check(bad == 0, $"{bad} of {logs} fallen trees on rough ground have an end more than 0.3 m from the exact ground (worst {worst:F2} m)");
    }

    // ------------------------------------------------------------------ determinism

    private List<ScatterRecord>[] BuildAll(IReadOnlyList<Tile> order, int workers)
    {
        var results = new List<ScatterRecord>[order.Count];
        var samplers = new RingTerrainSampler[workers];
        for (int i = 0; i < workers; i++) samplers[i] = _root.CreateIndependentCopy();
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
        {
            for (int i = k; i < order.Count; i += workers) results[i] = BuildTile(samplers[k], order[i].Key, out _, out _);
        });
        foreach (RingTerrainSampler s in samplers) s.Dispose();
        return results;
    }

    private void Determinism()
    {
        List<ScatterRecord>[] one = BuildAll(_tiles, 1);
        List<ScatterRecord>[] again = BuildAll(_tiles, 1);
        List<Tile> reversed = _tiles.AsEnumerable().Reverse().ToList();
        List<ScatterRecord>[] rev = BuildAll(reversed, 1);
        List<ScatterRecord>[] four = BuildAll(_tiles, 4);
        int mismatch = 0;
        for (int i = 0; i < _tiles.Count; i++)
        {
            if (!one[i].SequenceEqual(_tiles[i].Records)) mismatch++;
            if (!one[i].SequenceEqual(again[i])) mismatch++;
            if (!one[i].SequenceEqual(rev[_tiles.Count - 1 - i])) mismatch++;
            if (!one[i].SequenceEqual(four[i])) mismatch++;
        }
        Check(mismatch == 0, $"{mismatch} tile comparisons differ across repeat, reversed order and 1 vs 4 workers");
        Detail($"determinism: {_tiles.Count} tiles x 4 comparisons, {mismatch} mismatches");
    }

    // ------------------------------------------------------------------ rocks-only world unchanged

    private void RocksUnchanged()
    {
        RingScatterClassTable prod = RingScatterClassTable.Production;
        RingScatterClassTable rocks = RingScatterClassTable.CreateForTests(prod.Rows.Where(r => r.FeatureBit == 4));
        Check(prod.HashFor(1 << 4) == rocks.Hash, "HashFor(bit 4) differs from the three-row rock table hash");
        LandscapeWorld.Spec spec = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" });
        using RingTerrainSampler rockWorld = spec.CreateSampler();
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        int compared = 0;
        foreach (string name in new[] { "steep_eye", "scree_eye" })
        {
            LandscapePose p = dressing.Poses.First(x => x.Name == name);
            long ta = (long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            var viaProduction = RingBiomeDecorationLayer.BuildScatterReviewTile(rockWorld, ta, tz, LandscapeWorld.Seed, Circ, Width, prod);
            var viaRocks = RingBiomeDecorationLayer.BuildScatterReviewTile(rockWorld, ta, tz, LandscapeWorld.Seed, Circ, Width, rocks);
            Check(viaProduction.Count > 0 && viaProduction.SequenceEqual(viaRocks), $"{name}: the rocks-only world's records differ with the sapling row in the production table");
            Check(viaProduction.All(r => r.ClassId != Sapling), $"{name}: a sapling record in a world without bit 6");
            compared += viaProduction.Count;
        }
        // A world with both bits builds the rock rows too: their records equal the rocks-only world's (the dead-wood rows avoid nothing of theirs and nothing avoids them).
        LandscapeWorld.Spec both = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks", "vegetation.forest_structure" });
        using RingTerrainSampler bothWorld = both.CreateSampler();
        foreach (string name in new[] { "steep_eye", "scree_eye" })
        {
            LandscapePose p = dressing.Poses.First(x => x.Name == name);
            long ta = (long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            var rockOnly = RingBiomeDecorationLayer.BuildScatterReviewTile(rockWorld, ta, tz, LandscapeWorld.Seed, Circ, Width, prod);
            var bothRocks = RingBiomeDecorationLayer.BuildScatterReviewTile(bothWorld, ta, tz, LandscapeWorld.Seed, Circ, Width, prod).Where(r => r.ClassId <= 3).ToList();
            Check(rockOnly.Count > 0 && rockOnly.SequenceEqual(bothRocks), $"{name}: the rock records of a world with bits 4 and 6 differ from the rocks-only world's");
        }
        Detail($"rocks-only world: {compared} records at steep_eye and scree_eye equal the rock-only table's; hash {prod.HashFor(1 << 4):X16}");
    }

    // ------------------------------------------------------------------ informational (g)

    private void Report()
    {
        foreach (string pose in ForestPoses.Append("plains_eye"))
        {
            List<Tile> ts = _tiles.Where(t => t.Pose == pose).ToList();
            double ha = ts.Count * _tileA * 240.0 / 10000.0;
            int n = ts.Sum(t => Saplings(t).Count());
            double stage = ts.Average(t => t.StageMs), total = ts.Average(t => t.BuildMs), worst = ts.Max(t => t.BuildMs);
            Tile centre = ts[ts.Count / 2];
            var trees = TreesAround(centre, out _);
            var obstacles = new List<(double A, double Z, double Width)>();
            foreach (var t in trees) obstacles.Add((t.A, t.Z, 0.6));
            foreach (Tile t in ts)
                foreach (ScatterRecord r in Saplings(t))
                    obstacles.Add((r.AroundCm * .01, r.AcrossCm * .01, ImportedTreeSourceTable.Row(r.Mesh).Species == TreeSpecies.Maple ? 2.2 : 0.0));
            double free = FreePath(centre, obstacles);
            // T6-7: the shrub row (6 candidates per 8 m cell) is the costliest row of the tile build; its stage is timed per tile as rows 0-1 (snag, shrub) minus row 0 (best of two, the window preparation is in both).
            double[] shrubStage = ts.Select(ShrubStageMs).ToArray();
            double shrubMs = shrubStage.Average(), shrubWorst = shrubStage.Max();
            Detail($"{pose}: saplings {n / ha:F1}/ha ({n} in {ha:F1} ha), shrubs {ts.Sum(t => ShrubRecords(t).Count()) / ha:F1}/ha, stage {stage:F2} ms/tile, BuildScatterRows {total:F2} ms/tile (worst {worst:F2}) of which the shrub row {shrubMs:F1} ms/tile (worst {shrubWorst:F1}), eye-level free path {free:F1} m (trees + saplings)");
            // T6-4 limited the whole scatter build of a tile (then saplings only) to 25 ms. T6-7: the shrub row (about 5,400 candidates per tile) and the sapling row's avoid scan over the shrubs raise it to 30 to 70 ms on a worker (measured: 7 to 15 ms before the shrubs); the limits are 150 ms per tile (the machine is shared and noisy) and 60 ms for the shrub row.
            if (pose != "plains_eye") Check(worst < 150.0, $"{pose}: the scatter build took {worst:F1} ms on a tile (limit 150 ms)");
            // Stand filler (user 2026-11-20): the shrub-stage limit is 75 ms (was 60); the stand-filler row in the same build pushed the shrub stage to about 60 ms at forest_interior_eye.
            if (pose != "plains_eye") Check(shrubWorst < 75.0, $"{pose}: the shrub stage took {shrubWorst:F1} ms on a tile (limit 75 ms)");
        }
    }

    private double ShrubStageMs(Tile t)
    {
        double Best(int rows)
        {
            double best = double.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                var trees = _ctx.BuildTreeSet(_root, t.Key);
                using RingTerrainSampler copy = _root.CreateIndependentCopy();
                var sw = Stopwatch.StartNew();
                RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, t.Key, trees, Array.Empty<ScatterRecord>(), 0, rows, 0xFFFF);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }
        Check(_ctx.Table.BuildIndexOf(Shrub) == 1, "the shrub row is not second in the build order (after the snag row, before the saplings)");
        return Math.Max(0.0, Best(2) - Best(1));
    }

    private double FreePath(Tile t, List<(double A, double Z, double Width)> obstacles)
    {
        var rng = new Random(77);
        double sum = 0; int rays = 300;
        for (int i = 0; i < rays; i++)
        {
            double a0 = t.A0 + rng.NextDouble() * _tileA, z0 = t.Z0 + 20.0 + rng.NextDouble() * 200.0, ang = rng.NextDouble() * Math.Tau;
            double dx = Math.Cos(ang), dz = Math.Sin(ang), best = 200.0;
            foreach ((double a, double z, double w) in obstacles)
            {
                double r = w * 0.5, cx = WrapDelta(a - a0), cz = z - z0, along = cx * dx + cz * dz;
                if (along < 0) continue;
                double perp2 = cx * cx + cz * cz - along * along;
                if (perp2 >= r * r) continue;
                best = Math.Min(best, Math.Max(0.0, along - Math.Sqrt(r * r - perp2)));
            }
            sum += best;
        }
        return sum / rays;
    }
}
