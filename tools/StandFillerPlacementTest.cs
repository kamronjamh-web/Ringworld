using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Vegetation.Understory;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using Ctx = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterBuildContext;
using Key = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTileKey;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6 stand filler (docs/work-orders/terrain-vegetation/T6-stand-filler-design.md, order SF-2): the near-only sub-canopy scatter row Id 15 (veg.standfill) in a vegetation.forest_structure world.
/// Density in fully Dense tiles is about 150 per ha for young stands (stand age under 0.2) and about 80 per ha for old ones (over 0.8), falling monotonically over the age terciles; no record where Dense and OpenWoodland are both 0,
/// in water, on site masks or on slopes over 34 degrees, or within 2.5 m of a canopy trunk; fillers stand in gaps (mean NearestTreeM above that of random points of the same terrain); variants only small maple and small pine (9-11, 27-29);
/// records identical across repeated builds and 1 vs 2 workers; the rocks-only and generation 1 class tables are unchanged. Prints STAND_FILLER_PLACEMENT=PASS. Headless: tools/run_tests.ps1 -Tests stand_filler_placement_test.
/// </summary>
public partial class StandFillerPlacementTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const ushort Filler = 15;
    /// <summary>HashFor(1 &lt;&lt; 4) before the stand filler row was added (the three rock rows only).</summary>
    private const ulong RocksOnlyHash = 0x68D80AE92AE98545UL;
    private static readonly long Cells = (long)Math.Ceiling(Circ / 30.0);
    private static readonly double AroundCell = Circ / Math.Ceiling(Circ / 30.0);
    private static readonly string[] ForestPoses = { "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye" };
    private static readonly int[] Allowed = { 9, 10, 11, 27, 28, 29 };

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("STAND_FILLER_PLACEMENT_DETAIL " + text);

    private sealed class Tile
    {
        public Key Key;
        public double A0, Z0;
        public List<ScatterRecord> Records = new();
    }

    private RingTerrainSampler _root = null!, _probe = null!;
    private RingHabitatFieldGrid _grid = null!;
    private ForestStructureSampler _structure = null!;
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
            Gaps();
            Variants();
            Determinism();
            Tables();
            Cost();
        }
        catch (Exception e) { Console.Error.WriteLine("STAND_FILLER_PLACEMENT=FAIL exception " + e); return 1; }
        finally { _probe?.Dispose(); _root?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("STAND_FILLER_PLACEMENT=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("STAND_FILLER_PLACEMENT=PASS");
        return 0;
    }

    private static IEnumerable<ScatterRecord> Fillers(Tile t) => t.Records.Where(r => r.ClassId == Filler);
    private static long Mod(long v, long n) { long r = v % n; return r < 0 ? r + n : r; }
    private static double WrapDelta(double d) => d > Circ * .5 ? d - Circ : d < -Circ * .5 ? d + Circ : d;

    private void Setup()
    {
        LandscapeWorld.Spec spec = LandscapeWorld.For(1, new[] { "vegetation.forest_structure" });
        _root = spec.CreateSampler();
        Check((_root.WorldFeatureMask & (1 << 6)) != 0 && (_root.WorldFeatureMask & (1 << 4)) == 0, "the test world must have bit 6 and not bit 4");
        _ctx = Ctx.ForReview(LandscapeWorld.Seed, Circ, Width, RingScatterClassTable.Production, null, _root.WorldFeatureMask);
        Check(_ctx.Table.Find(Filler) is { FeatureBit: 6, RenderGroup: RingScatterRenderGroup.StandFill }, "the bit 6 table has no stand filler row (Id 15)");
        _tileA = _ctx.TileCells * _ctx.AroundCellMeters;
        _probe = _root.CreateIndependentCopy();
        _grid = new RingHabitatFieldGrid(_probe, _ctx.Edits, LandscapeWorld.Seed, Circ, Width);
        _structure = ForestStructureSampler.For(_probe);
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        int lastZ = (Width - 1) / 240;
        var seen = new HashSet<Key>();
        foreach (string name in ForestPoses)
        {
            LandscapePose p = t0.Poses.First(x => x.Name == name);
            long ta = (long)Math.Floor(Math.Floor(p.Around / AroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            for (int dz = -2; dz <= 2; dz++)
                for (int da = -2; da <= 2; da++)
                {
                    long z = tz + dz;
                    if (z < 0 || z > lastZ) continue;
                    long a = Mod(ta + da, _ctx.AroundTileCount);
                    var key = new Key(a, z);
                    if (seen.Add(key)) _tiles.Add(new Tile { Key = key, A0 = a * _tileA, Z0 = z * 240.0 });
                }
        }
    }

    private List<ScatterRecord> BuildTile(RingTerrainSampler sampler, Key key)
    {
        var trees = _ctx.BuildTreeSet(sampler, key);
        using RingTerrainSampler copy = sampler.CreateIndependentCopy();
        return RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, key, trees, Array.Empty<ScatterRecord>(), 0, _ctx.Table.BuildOrder.Count, 0xFFFF).Records;
    }

    private void Build()
    {
        foreach (Tile t in _tiles) t.Records = BuildTile(_root, t.Key);
        Detail($"built {_tiles.Count} tiles, {_tiles.Sum(t => Fillers(t).Count())} filler records");
        Check(_tiles.Sum(t => Fillers(t).Count()) > 0, "no filler records at all");
        // Identity fingerprint of every filler record (FNV over id, position, mesh, size): compare before and after any change to the candidate evaluation.
        ulong h = 0xCBF29CE484222325UL;
        foreach (Tile t in _tiles)
            foreach (ScatterRecord r in Fillers(t))
                foreach (long v in new long[] { (long)r.Id, r.AroundCm, r.AcrossCm, r.Mesh, BitConverter.SingleToInt32Bits(r.Size), BitConverter.SingleToInt32Bits(r.Yaw) })
                { h ^= (ulong)v; h *= 0x100000001B3UL; }
        Detail($"filler record hash {h:X16}");
    }

    // ------------------------------------------------------------------ fields at a point

    private struct Pt { public bool Usable; public float Dense, Open, Age; public RingHabitatSample F; }

    private Pt At(double a, double z)
    {
        RingHabitatSample f = _grid.Sample(a, z);
        var regional = new ForestRegionSample(f.Habitat, f.Canopy, 0f, f.Clearing, f.MapleWeight, f.FirWeight, f.PineWeight);
        ForestStructureSampler.SpeciesWeights(f.Moisture, f.Temperature, f.WetnessTotal, f.Relief64, f.RockExposure, f.SlopeDeg, in regional, out _, out float fir, out float pine);
        RingCommunityMemberships m = RingPlantCommunities.Memberships(f.Canopy, f.Clearing, fir + pine, f.WetnessTotal, f.Relief64, f.RockExposure, f.Moisture, f.Scree);
        float age = ForestStructureSampler.EffectiveAge(_structure.ContrastedStandAge((long)Math.Floor(a), (int)Math.Floor(z)), f.Canopy, f.Clearing, f.Temperature);
        bool usable = (f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) == 0 && f.SlopeDeg < 28f;
        return new Pt { Usable = usable, Dense = m.ClosedBroadleaf + m.ConiferStand, Open = m.OpenWoodland, Age = age, F = f };
    }

    private IEnumerable<(double A, double Z)> Lattice(Tile t)
    {
        for (double a = t.A0 + 4.0; a < t.A0 + _tileA; a += 8.0)
            for (double z = t.Z0 + 4.0; z < t.Z0 + 240.0; z += 8.0)
            {
                if (z < 4.0 || z > Width - 4.0) continue;
                yield return (a, z);
            }
    }

    // ------------------------------------------------------------------ density

    private void Density()
    {
        const float FullDense = 0.95f;
        var cells = new List<(float Age, double Area)>();
        var fillerAges = new List<float>();
        foreach (Tile t in _tiles)
        {
            foreach ((double a, double z) in Lattice(t))
            {
                Pt p = At(a, z);
                if (p.Usable && p.Dense >= FullDense) cells.Add((p.Age, 64.0));
            }
            foreach (ScatterRecord r in Fillers(t))
            {
                Pt p = At(r.AroundCm * .01, r.AcrossCm * .01);
                if (p.Usable && p.Dense >= FullDense) fillerAges.Add(p.Age);
            }
        }
        double Area(Func<float, bool> sel) => cells.Where(c => sel(c.Age)).Sum(c => c.Area) / 10000.0;
        int Count(Func<float, bool> sel) => fillerAges.Count(sel);
        double young = Count(a => a < 0.2f) / Math.Max(1e-9, Area(a => a < 0.2f)), old = Count(a => a > 0.8f) / Math.Max(1e-9, Area(a => a > 0.8f));
        Detail($"fully Dense area {cells.Sum(c => c.Area) / 10000.0:F1} ha, {fillerAges.Count} fillers; stand age < 0.2: {young:F1}/ha ({Count(a => a < 0.2f)} in {Area(a => a < 0.2f):F1} ha), > 0.8: {old:F1}/ha ({Count(a => a > 0.8f)} in {Area(a => a > 0.8f):F1} ha)");
        Check(Area(a => a < 0.2f) >= 3.0 && Area(a => a > 0.8f) >= 3.0, $"too little fully Dense area to compare (young {Area(a => a < 0.2f):F1} ha, old {Area(a => a > 0.8f):F1} ha)");
        Check(young >= 130.0 && young <= 170.0, $"young-stand density {young:F1}/ha is outside 130-170");
        Check(old >= 65.0 && old <= 100.0, $"old-stand density {old:F1}/ha is outside 65-100");
        float[] ages = cells.Select(c => c.Age).OrderBy(x => x).ToArray();
        if (ages.Length >= 3)
        {
            float c1 = ages[ages.Length / 3], c2 = ages[2 * ages.Length / 3];
            double d0 = Count(a => a < c1) / Math.Max(1e-9, Area(a => a < c1)), d1 = Count(a => a >= c1 && a < c2) / Math.Max(1e-9, Area(a => a >= c1 && a < c2)), d2 = Count(a => a >= c2) / Math.Max(1e-9, Area(a => a >= c2));
            Detail($"density per ha by stand-age tercile (cuts {c1:F2}, {c2:F2}): {d0:F1}, {d1:F1}, {d2:F1}");
            Check(d0 > d1 && d1 > d2, $"filler density does not fall over the stand-age terciles ({d0:F1}, {d1:F1}, {d2:F1})");
        }
    }

    // ------------------------------------------------------------------ exclusions

    private List<(double A, double Z)> TreesAround(Tile t)
    {
        var trees = new List<(double, double)>();
        var seen = new HashSet<Key>();
        for (int dz = -1; dz <= 1; dz++)
            for (int da = -1; da <= 1; da++)
            {
                long z = t.Key.Across + dz; if (z < 0) continue;
                var k = new Key(Mod(t.Key.Around + da, _ctx.AroundTileCount), z);
                if (!seen.Add(k)) continue;
                foreach (var rec in RingBiomeDecorationLayer.BuildImportedForestReviewTile(_root, k.Around, k.Across, Cells, AroundCell, LandscapeWorld.Seed, Circ, Width))
                    trees.Add((rec.Around, rec.Across));
            }
        return trees;
    }

    private void Exclusions()
    {
        int total = 0, blocked = 0, water = 0, steep = 0, nearTrunk = 0, noHabitat = 0;
        double minTrunk = double.MaxValue;
        foreach (Tile t in _tiles)
        {
            if (!Fillers(t).Any()) continue;
            var trees = TreesAround(t);
            foreach (ScatterRecord r in Fillers(t))
            {
                total++;
                double a = r.AroundCm * .01, z = r.AcrossCm * .01;
                Pt p = At(a, z);
                if ((p.F.Flags & RingHabitatFlags.SiteBlocked) != 0) blocked++;
                if ((p.F.Flags & RingHabitatFlags.Water) != 0) water++;
                if (p.F.SlopeDeg > 34.0001f) steep++;
                if (p.Dense <= 1e-6f && p.Open <= 1e-6f) noHabitat++;
                foreach ((double ta, double tz) in trees)
                {
                    double da = WrapDelta(ta - a), dz = tz - z, d = Math.Sqrt(da * da + dz * dz);
                    if (d < minTrunk) minTrunk = d;
                    if (d < 2.5 - 1e-6) { nearTrunk++; break; }
                }
            }
        }
        Check(total > 0, "no filler records");
        Check(noHabitat == 0, $"{noHabitat} fillers stand where Dense and OpenWoodland are both 0");
        Check(blocked == 0, $"{blocked} fillers on a site mask");
        Check(water == 0, $"{water} fillers in water");
        Check(steep == 0, $"{steep} fillers on slopes over 34 degrees");
        Check(nearTrunk == 0, $"{nearTrunk} fillers within 2.5 m of an accepted tree root");
        Detail($"exclusions over {total} fillers: no habitat {noHabitat}, site {blocked}, water {water}, steep {steep}, nearest trunk {minTrunk:F2} m (limit 2.5)");
    }

    // ------------------------------------------------------------------ gap preference

    private void Gaps()
    {
        double fillerSum = 0.0, randomSum = 0.0; long n = 0;
        var rng = new Random(4242);
        foreach (Tile t in _tiles)
        {
            List<ScatterRecord> mine = Fillers(t).ToList();
            if (mine.Count == 0) continue;
            var trees = TreesAround(t);
            double Nearest(double a, double z)
            {
                double best = 50.0;
                foreach ((double ta, double tz) in trees)
                {
                    double da = WrapDelta(ta - a), dz = tz - z;
                    if (Math.Abs(da) >= best || Math.Abs(dz) >= best) continue;
                    best = Math.Min(best, Math.Sqrt(da * da + dz * dz));
                }
                return best;
            }
            var terrain = new List<(double A, double Z)>();
            foreach ((double a, double z) in Lattice(t)) { Pt p = At(a, z); if (p.Usable && p.Dense >= 0.5f) terrain.Add((a, z)); }
            if (terrain.Count == 0) continue;
            foreach (ScatterRecord r in mine) { fillerSum += Nearest(r.AroundCm * .01, r.AcrossCm * .01); n++; }
            for (int i = 0; i < mine.Count; i++)
            {
                (double a, double z) = terrain[rng.Next(terrain.Count)];
                randomSum += Nearest(a + (rng.NextDouble() - 0.5) * 8.0, Math.Clamp(z + (rng.NextDouble() - 0.5) * 8.0, 0.0, Width - 1.0));
            }
        }
        Check(n > 0, "no fillers in tiles with Dense terrain for the gap comparison");
        if (n == 0) return;
        Detail($"mean NearestTreeM: fillers {fillerSum / n:F2} m, random Dense terrain points {randomSum / n:F2} m ({n} each)");
        Check(fillerSum > randomSum, $"fillers do not prefer gaps: mean NearestTreeM {fillerSum / n:F2} m is not above random points {randomSum / n:F2} m");
    }

    // ------------------------------------------------------------------ variants

    private void Variants()
    {
        int bad = 0; var counts = new Dictionary<int, int>();
        foreach (ScatterRecord r in _tiles.SelectMany(Fillers))
        {
            if (!Allowed.Contains(r.Mesh)) bad++;
            counts[r.Mesh] = counts.GetValueOrDefault(r.Mesh) + 1;
            if (UnderstoryCatalog.StandFillRowOf(r.Mesh) < 0) bad++;
        }
        Check(bad == 0, $"{bad} fillers use a variant outside 9-11 and 27-29 (fir has none)");
        Detail("variants: " + string.Join(", ", counts.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")));
        Check(UnderstoryCatalog.StandFillSources.SequenceEqual(Allowed), "UnderstoryCatalog.StandFillSources is not 9, 10, 11, 27, 28, 29");
    }

    // ------------------------------------------------------------------ determinism

    private List<ScatterRecord>[] BuildAll(IReadOnlyList<Tile> order, int workers)
    {
        var results = new List<ScatterRecord>[order.Count];
        var samplers = new RingTerrainSampler[workers];
        for (int i = 0; i < workers; i++) samplers[i] = _root.CreateIndependentCopy();
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
        {
            for (int i = k; i < order.Count; i += workers) results[i] = BuildTile(samplers[k], order[i].Key);
        });
        foreach (RingTerrainSampler s in samplers) s.Dispose();
        return results;
    }

    private void Determinism()
    {
        List<Tile> sub = _tiles.Where(t => Fillers(t).Any()).Take(12).ToList();
        Check(sub.Count > 0, "no tiles with fillers for the determinism check");
        List<ScatterRecord>[] one = BuildAll(sub, 1), two = BuildAll(sub, 2);
        int mismatch = 0;
        for (int i = 0; i < sub.Count; i++)
        {
            List<ScatterRecord> mine = Fillers(sub[i]).ToList();
            if (!one[i].Where(r => r.ClassId == Filler).SequenceEqual(mine)) mismatch++;
            if (!two[i].Where(r => r.ClassId == Filler).SequenceEqual(mine)) mismatch++;
        }
        Check(mismatch == 0, $"{mismatch} tile comparisons differ between the first build, a second build and 2 workers");
        Detail($"determinism: {sub.Count} tiles x 2 comparisons, {mismatch} mismatches");
    }

    // ------------------------------------------------------------------ tables

    private void Tables()
    {
        RingScatterClassTable prod = RingScatterClassTable.Production;
        RingScatterClassTable rocks = RingScatterClassTable.CreateForTests(prod.Rows.Where(r => r.FeatureBit == 4));
        ulong hash = prod.HashFor(1 << 4);
        Check(hash == rocks.Hash, "HashFor(bit 4) differs from the three-row rock table hash");
        Check(hash == RocksOnlyHash, $"HashFor(bit 4) {hash:X16} differs from the value before the stand filler row ({RocksOnlyHash:X16})");
        Check(prod.ForMask(0).Rows.Count == 0, "ForMask(0) has rows (generation 1 must see no scatter rows)");
        Check(prod.HashFor(1 << 6) != hash, "the bit 6 table hash did not change");
        Detail($"HashFor(bit 4) {hash:X16}; ForMask(0) rows {prod.ForMask(0).Rows.Count}; bit 6 table {prod.HashFor(1 << 6):X16}");
    }

    // ------------------------------------------------------------------ cost (informational)

    private void Cost()
    {
        int index = _ctx.Table.BuildIndexOf(Filler);
        foreach (Tile t in _tiles.Where(t => Fillers(t).Any()).OrderBy(t => Fillers(t).Count()).Where((t, i) => i % 14 == 0).Take(3))
        {
            double Best(int rows)
            {
                double best = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    var trees = _ctx.BuildTreeSet(_root, t.Key);
                    using RingTerrainSampler copy = _root.CreateIndependentCopy();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    RingBiomeDecorationLayer.BuildScatterRows(_ctx, copy, t.Key, trees, Array.Empty<ScatterRecord>(), 0, rows, 0xFFFF);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                return best;
            }
            double without = Best(index), with = Best(index + 1);
            Detail($"cost: tile {t.Key.Around},{t.Key.Across} with {Fillers(t).Count()} fillers: rows before the filler {without:F1} ms, with the filler {with:F1} ms (filler row {with - without:F1} ms)");
        }
    }
}
