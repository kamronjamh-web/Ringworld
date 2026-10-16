using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>S3 placement test: card/low placement, Bayer rank nesting, tier filtering, masks, curve and the
/// blade-golden guard. Run via tools/grass_card_placement_test.gd.</summary>
public partial class GrassCardPlacementTest : RefCounted
{
    private const long Circumference = EmitterSiteTestWorlds.Circumference;
    private const int Width = EmitterSiteTestWorlds.Width;
    private const float Tile = 32f;
    private const int Stride = RingGrassTileData.FloatsPerInstance;
    private readonly List<string> _failures = new();
    private RingLodSurfaceEditSnapshot _clean = null!;

    // Pre-change output of the blade builder (committed HEAD behaviour) on this fixture: placement hash, count, capped hash/count.
    private static readonly Dictionary<string, string> BladeGolden = new()
    {
        ["legacy/f1/plain"] = "0xB7009571D7A89355 n=47 cap300=0xB7009571D7A89355/47",
        ["legacy/f1/mods"] = "0x1F31FE28E7498714 n=38 cap300=0xB7009571D7A89355/47",
        ["legacy/f0.49/plain"] = "0x8DF56849F91B07D8 n=27 cap300=0x8DF56849F91B07D8/27",
        ["legacy/f0.49/mods"] = "0xD232FEFA1334EF6B n=22 cap300=0x8DF56849F91B07D8/27",
        ["legacy/f0.09/plain"] = "0x2D6928E2B7BD3732 n=4 cap300=0x2D6928E2B7BD3732/4",
        ["legacy/f0.09/mods"] = "0x2D6928E2B7BD3732 n=4 cap300=0x2D6928E2B7BD3732/4",
        ["dense/f1/plain"] = "0x25B005EC6128AD11 n=1916 cap300=0x47E7238D765CBB94/300",
        ["dense/f1/mods"] = "0xD6649E4D47A1066B n=1520 cap300=0x47E7238D765CBB94/300",
        ["dense/f0.49/plain"] = "0xD412C536DC571D51 n=983 cap300=0xE01EE869F86FD226/300",
        ["dense/f0.49/mods"] = "0xD1B514929E65E03D n=766 cap300=0xE01EE869F86FD226/300",
        ["dense/f0.09/plain"] = "0x572636C9D865593D n=177 cap300=0x572636C9D865593D/177",
        ["dense/f0.09/mods"] = "0x6864FD3258FDF946 n=135 cap300=0x572636C9D865593D/177",
    };

    // Card placement golden (recorded on the S5 tree): PlacementHash and InstanceCount per case.
    private static readonly Dictionary<string, string> CardGolden = new()
    {
        ["meadow/t0"] = "0xE22087CF5A8C6BC8 n=16660 low=9381",
        ["meadow/t4"] = "0x72604404683CDB19 n=1774 low=0",
        ["meadow/t8"] = "0x7010F6E1E6F03AF1 n=464 low=0",
        ["meadow/t13"] = "0xF94FD8E2252DBA2B n=88 low=0",
        ["seam-last/t0"] = "0xFD3C2F22272CF8DA n=6939 low=4009",
        ["seam-first/t0"] = "0xBDCFE46AD30BD412 n=7009 low=3965",
    };

    public bool Run()
    {
        _clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        try
        {
            TestCurve();
            TestBayer();
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            TestBladeGolden(sampler);
            TestCardGolden(sampler);
            (long a, long b) meadow = FindMeadowTile(sampler);
            TestPayloadAndDeterminism(sampler, meadow);
            TestFilterEquivalence(sampler, meadow);
            TestStratification(sampler, meadow);
            TestSeamAndTileInvariance(sampler);
            TestExclusionsAndModifiers(sampler, meadow);
            TestSlope(sampler, meadow);
            TestWater(sampler);
            TestHeightByBiome(sampler);
            TestSiteMask();
            ReportCostAndCounts(sampler, meadow);
        }
        catch (Exception exception) { _failures.Add("exception: " + exception); }
        if (_failures.Count > 0)
        {
            foreach (string failure in _failures) GD.PushError("GRASS_CARD_PLACEMENT fail: " + failure);
            GD.Print("GRASS_CARD_PLACEMENT=FAIL " + _failures.Count);
            return false;
        }
        GD.Print("GRASS_CARD_PLACEMENT=PASS");
        return true;
    }

    private void Check(bool condition, string message) { if (!condition) _failures.Add(message); }

    // ------------------------------------------------------------------ fixtures

    private static RingLodGeometrySnapshot Geometry(RingCanonicalPosition anchor, Vector3? origin = null)
        => new(Transform3D.Identity, Vector3.Zero, origin ?? Vector3.Zero, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, anchor);

    private readonly record struct TileFixture(RingGrassTileKey Key, RingGrassTileBounds Bounds, RingLodGeometrySnapshot Geometry);

    private static TileFixture Fixture(long tileAround, long tileAcross, float size = Tile)
    {
        var key = new RingGrassTileKey(tileAround, tileAcross);
        RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(tileAround, tileAcross, size, Circumference, Width);
        return new TileFixture(key, bounds, Geometry(new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter)));
    }

    private RingGrassTileData BuildCards(RingTerrainSampler sampler, TileFixture t, float fraction = 1, byte tier = 0, bool low = true,
        int cap = 100_000, int lowCap = 100_000, float slope = 25, RingGrassExclusionSnapshot? exclusions = null,
        RingGrassModifierSnapshot? modifiers = null, RingGrassAppearanceSnapshot appearance = default, RingGrassCardPlacement? options = null)
    {
        RingGrassCardPlacement cards = (options ?? new RingGrassCardPlacement()) with { IncludeLowLayer = low, LowLayerMaxInstances = lowCap };
        return RingGrassTileBuilder.Build(t.Key, t.Bounds, 8, 4, 0, cap, slope, sampler, _clean, t.Geometry, CancellationToken.None,
            exclusions: exclusions, modifiers: modifiers, appearance: appearance, sourceFraction: fraction, sourceTier: tier, cards: cards);
    }

    private (long, long) FindMeadowTile(RingTerrainSampler sampler)
    {
        (long, long) best = (186_000 / 32, 141_000 / 32);
        int bestCount = -1;
        for (int dz = 0; dz < 6; dz++) for (int da = 0; da < 6; da++)
        {
            var t = Fixture(186_000 / 32 + da * 5, 141_000 / 32 + dz * 5);
            int n = BuildCards(sampler, t, low: false, options: null).InstanceCount;
            if (n > bestCount) { bestCount = n; best = (186_000 / 32 + da * 5, 141_000 / 32 + dz * 5); }
        }
        Check(bestCount > 1500, $"meadow fixture too sparse ({bestCount} cards)");
        GD.Print($"GRASS_CARD[fixture] meadow tile {best} cards_at_full={bestCount}");
        return best;
    }

    /// <summary>Canonical position, local buffer values and decoded payload of one instance.</summary>
    private readonly record struct Inst(double Around, double Across, float Height, float Width, int Kind, int Variant, float RankFromCustom,
        float C0, float C1, float C2, float C3, float W0, float W1, float W2, float W3, float Key);

    private static List<Inst> Decode(RingGrassTileData d)
    {
        var list = new List<Inst>(d.InstanceCount);
        for (int i = 0; i < d.InstanceCount; i++)
        {
            int o = i * Stride;
            float[] b = d.Buffer;
            float custom = b[o + 16];
            int whole = (int)Math.Floor(custom);
            float height = new Vector3(b[o + 1], b[o + 5], b[o + 9]).Length();
            float width = new Vector3(b[o], b[o + 4], b[o + 8]).Length();
            list.Add(new Inst(d.Bounds.AroundCenter + b[o + 11], d.Bounds.AcrossCenter + b[o + 3], height, width, whole / 4, whole % 4,
                (custom - whole) / .999f, custom, b[o + 17], b[o + 18], b[o + 19], b[o + 12], b[o + 13], b[o + 14], b[o + 15],
                d.TierKeys != null ? d.TierKeys[i] : -1));
        }
        return list;
    }

    private static string Sig(Inst i) => $"{i.C0:R}|{i.C1:R}|{i.C2:R}|{i.C3:R}|{i.W0:R}|{i.W1:R}|{i.W2:R}|{i.W3:R}";

    private static bool SameBuffer(RingGrassTileData a, RingGrassTileData b)
        => a.InstanceCount == b.InstanceCount && a.PlacementHash == b.PlacementHash && a.Buffer.AsSpan().SequenceEqual(b.Buffer);

    // ------------------------------------------------------------------ pure tests

    private void TestBayer()
    {
        var seen = new bool[256];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) seen[RingGrassCardRank.Bayer16(x, y)] = true;
        Check(seen.All(v => v), "Bayer16 is not a permutation of 0..255");
        Check(RingGrassCardRank.Bayer16(0, 0) == 0 && RingGrassCardRank.Bayer16(16, 32) == 0 && RingGrassCardRank.Bayer16(-16, 0) == 0, "Bayer16 does not tile with period 16");
        // Every aligned 4x4 block holds 16 distinct sixteenths of the rank range (stratification property).
        for (int by = 0; by < 16; by += 4) for (int bx = 0; bx < 16; bx += 4)
        {
            var sixteenths = new HashSet<int>();
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) sixteenths.Add(RingGrassCardRank.Bayer16(bx + x, by + y) / 16);
            Check(sixteenths.Count == 16, $"Bayer16 block ({bx},{by}) is not stratified ({sixteenths.Count}/16 distinct sixteenths)");
        }
        Check(RingGrassCardRank.ToRank(uint.MaxValue) < 1f && RingGrassCardRank.ToRank(0) == 0f, "ToRank must stay inside [0,1)");
    }

    private void TestCurve()
    {
        var p = RingGrassCardCurveParameters.Default;
        void Near(string name, float actual, float expected, float tolerance = 1e-4f) => Check(Math.Abs(actual - expected) <= tolerance, $"curve {name}: {actual:R} != {expected:R}");
        static double S(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        // Independent double-precision evaluation of the plan formulas.
        double Far(double d) => Math.Clamp(2.5 * Math.Pow(20.0 / Math.Max(d, 20.0), 1.3), .15, 2.5);
        double Dens(double d) => Far(d) + (14 - Far(d)) * (1 - S(8, 45, d));
        foreach (float d in new[] { 0f, 4f, 8f, 12f, 20f, 30f, 45f, 70f, 100f, 200f, 400f, 1000f })
        {
            Near($"density({d})", RingGrassCardCurve.Density(p, d), (float)Dens(d), 2e-4f);
            double keep = Math.Min(1, Dens(d) / 14);
            Near($"keep({d})", RingGrassCardCurve.Keep(p, d), (float)keep, 1e-5f);
            foreach (float rank in new[] { 0f, .01f, .05f, .3f, .9f })
            {
                double grow = S(0, 1, (keep - rank) / Math.Max(.02, .2 * keep));
                double expected = rank < keep && grow >= .08
                    ? Math.Min(1 / Math.Sqrt(Math.Min(1, Dens(d) / 2.5)), 2.5) * grow * (.7 + .3 * S(8, 45, d)) * (1 - S(340, 400, d))
                    : 0;
                Near($"scale({d},{rank})", RingGrassCardCurve.Scale(p, rank, d), (float)expected, 3e-4f);
                Check(RingGrassCardCurve.Accepts(p, rank, d) == (rank < keep && grow >= .08), $"curve accepts({d},{rank})");
            }
        }
        Near("density(0)", RingGrassCardCurve.Density(p, 0), 14f);
        Near("density(100)", RingGrassCardCurve.Density(p, 100), 2.5f * MathF.Pow(.2f, 1.3f), 1e-4f);
        Near("density(1000)=floor", RingGrassCardCurve.Density(p, 1000), .15f);
        Near("scale(rank0,100)=cap", RingGrassCardCurve.Scale(p, 0, 100), 2.5f);
        Near("scale(rank.5,100)=0", RingGrassCardCurve.Scale(p, .5f, 100), 0f);
        Near("fade(370)", RingGrassCardCurve.Fade(p, 370), .5f);
        Near("lowKeep(8)", RingGrassCardCurve.LowKeep(p, 8), 1f);
        Near("lowKeep(15)", RingGrassCardCurve.LowKeep(p, 15), .5f);
        Near("lowKeep(22)", RingGrassCardCurve.LowKeep(p, 22), 0f);
        Near("ladder(2)", RingGrassCardCurve.LadderFraction(2), .5f);
        Check(RingGrassCardCurve.LadderTierCount(p) == 14, $"ladder tier count {RingGrassCardCurve.LadderTierCount(p)} != 14");
        Check(RingGrassCardCurve.TierNeedsLowLayer(p, 25f, 4f) && !RingGrassCardCurve.TierNeedsLowLayer(p, 27f, 4f), "TierNeedsLowLayer boundary");
        // Monotone: the kept set at a farther distance is a subset (rank < keep falls with distance).
        float prev = 2;
        for (float d = 0; d <= 400; d += 5) { float k = RingGrassCardCurve.Keep(p, d); Check(k <= prev + 1e-6f, $"keep not monotone at {d}"); prev = k; }
    }

    private void TestBladeGolden(RingTerrainSampler sampler)
    {
        int matched = 0;
        foreach (string line in BladeGoldenLines(sampler))
        {
            int space = line.IndexOf(' ');
            string name = line[..space], value = line[(space + 1)..];
            if (!BladeGolden.TryGetValue(name, out string? expected)) { _failures.Add("blade golden: unknown case " + name); continue; }
            if (expected == value) matched++; else _failures.Add($"blade golden {name}: {value} != {expected} (blade placement changed)");
        }
        Check(matched == BladeGolden.Count, $"blade golden matched {matched}/{BladeGolden.Count}");
        GD.Print($"GRASS_CARD[blade_golden] matched={matched}/{BladeGolden.Count}");
    }

    private void TestCardGolden(RingTerrainSampler sampler)
    {
        (long a, long b) m = FindMeadowTile(sampler);
        long lastTile = RingGrassCoordinates.AroundTileCount(Circumference, Tile) - 1;
        long across = 141_000 / 32;
        for (long candidate = 2500; candidate < 9500; candidate += 37)
        {
            if (BuildCards(sampler, Fixture(lastTile, candidate), low: false).InstanceCount > 600 && BuildCards(sampler, Fixture(0, candidate), low: false).InstanceCount > 600) { across = candidate; break; }
        }
        var cases = new List<(string, TileFixture, int)>();
        foreach (int tier in new[] { 0, 4, 8, 13 }) cases.Add(($"meadow/t{tier}", Fixture(m.a, m.b), tier));
        cases.Add(("seam-last/t0", Fixture(lastTile, across), 0));
        cases.Add(("seam-first/t0", Fixture(0, across), 0));
        int matched = 0;
        foreach ((string name, TileFixture t, int tier) in cases)
        {
            RingGrassTileData d = BuildCards(sampler, t, RingGrassCardCurve.LadderFraction(tier), (byte)tier, tier == 0);
            string value = $"0x{d.PlacementHash:X16} n={d.InstanceCount} low={d.LowLayerInstanceCount}";
            GD.Print($"GRASS_CARD[card_golden_value] [\"{name}\"] = \"{value}\"");
            if (!CardGolden.TryGetValue(name, out string? expected)) { _failures.Add("card golden: unknown case " + name); continue; }
            if (expected == value) matched++; else _failures.Add($"card golden {name}: {value} != {expected} (card placement changed)");
        }
        Check(matched == CardGolden.Count && matched == cases.Count, $"card golden matched {matched}/{CardGolden.Count}");
        GD.Print($"GRASS_CARD[card_golden] matched={matched}/{CardGolden.Count}");
    }

    // ------------------------------------------------------------------ builder tests

    private void TestPayloadAndDeterminism(RingTerrainSampler sampler, (long a, long b) m)
    {
        var t = Fixture(m.a, m.b);
        RingGrassTileData first = BuildCards(sampler, t);
        RingGrassTileData again = BuildCards(sampler, t);
        Check(SameBuffer(first, again), "card build is not deterministic across runs");
        RingGrassTileData[] parallel = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => BuildCards(sampler, t)))).GetAwaiter().GetResult();
        Check(parallel.All(p => SameBuffer(first, p)), "card build differs across threads");
        // A different floating origin / focus changes only the geometry snapshot: the buffer must not move.
        var moved = new TileFixture(t.Key, t.Bounds, Geometry(new RingCanonicalPosition(t.Bounds.AroundCenter + 5000, 0, t.Bounds.AcrossCenter - 700), new Vector3(37, 5, -19)));
        Check(SameBuffer(first, BuildCards(sampler, moved)), "card build depends on the geometry origin / focus");

        List<Inst> insts = Decode(first);
        int low = first.LowLayerInstanceCount, main = first.InstanceCount - low;
        Check(first.IsCardPlacement && first.LowLayerIncluded && main > 1500 && low > 1500, $"card tile counts main={main} low={low}");
        Check(first.TierKeys != null && first.TierKeys.Length == first.InstanceCount, "card tile has no tier keys");
        bool sortedMain = true, sortedLow = true;
        for (int i = 1; i < main; i++) if (insts[i].Key < insts[i - 1].Key) sortedMain = false;
        for (int i = main + 1; i < insts.Count; i++) if (insts[i].Key < insts[i - 1].Key) sortedLow = false;
        Check(sortedMain && sortedLow, "card/low sections are not rank-sorted");
        Check(insts.Take(main).All(i => i.Kind == 0) && insts.Skip(main).All(i => i.Kind == 1), "kind flag does not match the buffer layout");
        Check(insts.All(i => i.Variant is >= 0 and < 4 && Math.Abs(i.RankFromCustom - i.Key) < 2e-6f), "CUSTOM.x does not encode kind*4 + variant + rank*0.999");
        Check(insts.All(i => i.W0 >= 0 && i.W1 >= 0 && i.W2 >= 0 && i.W3 >= 0 && i.W0 + i.W1 + i.W2 + i.W3 <= 1.0001f), "COLOR biome weights outside [0,1] simplex");
        Check(insts.Skip(main).All(i => i.Height is >= .105f and <= .21f && i.Width is >= .29f and <= .49f), "low layer height/width outside h .11-.20 / w .30-.48");
        Check(insts.Take(main).All(i => i.Height is > .2f and < .8f), "card heights outside the .3/.6 +-15% (+lean) band");
        Check(insts.Take(main).Select(i => i.Variant).Distinct().Count() == 4, "atlas variants are not all used");
        Check(insts.All(i => i.Around >= t.Bounds.AroundMin - 1e-3 && i.Around < t.Bounds.AroundMax + 1e-3 && i.Across >= t.Bounds.AcrossMin - 1e-3 && i.Across < t.Bounds.AcrossMax + 1e-3), "instance outside tile bounds");
        // Bilinear biome weights vary smoothly: not all instances share the weights of a 1 m cell.
        Check(insts.Take(main).Select(i => (i.W0, i.W1, i.W2, i.W3)).Distinct().Count() > 20, "biome weights are not per-root (bilinear)");
        Check(first.LocalAabb.Size.Y > 1f && first.LocalAabb.Size.X > Tile, "card AABB not widened for runtime scale");
    }

    private void TestFilterEquivalence(RingTerrainSampler sampler, (long a, long b) m)
    {
        var t = Fixture(m.a, m.b);
        RingGrassTileData probe = BuildCards(sampler, t);
        int capMain = (probe.InstanceCount - probe.LowLayerInstanceCount) * 6 / 10, capLow = probe.LowLayerInstanceCount * 6 / 10;
        foreach ((string name, int cap, int lowCap) in new[] { ("uncapped", 100_000, 100_000), ("capped", capMain, capLow) })
        {
            RingGrassTileData full = BuildCards(sampler, t, cap: cap, lowCap: lowCap);
            if (name == "capped") Check(full.InstanceCount - full.LowLayerInstanceCount == cap && full.LowLayerInstanceCount == lowCap, $"{name}: caps did not bind ({full.InstanceCount}, low {full.LowLayerInstanceCount})");
            var denser = new HashSet<string>(Decode(full).Select(Sig));
            RingGrassTileData? previous = full;
            for (int tier = 1; tier <= 12; tier++)
            {
                float f = RingGrassCardCurve.LadderFraction(tier);
                bool low = tier <= 3;
                RingGrassTileData fresh = BuildCards(sampler, t, f, (byte)tier, low, cap, lowCap);
                RingGrassTileData? direct = RingGrassTileBuilder.FilterCardTier(full, f, (byte)tier, low);
                Check(direct != null && SameBuffer(direct, fresh) && direct.SourceTier == tier && direct.LowLayerInstanceCount == fresh.LowLayerInstanceCount,
                    $"{name}: FilterCardTier(full) != fresh build at tier {tier} (f={f:0.###}, low={low})");
                // Chain: filtering an already filtered tile is also exact.
                RingGrassTileData? chained = previous == null ? null : RingGrassTileBuilder.FilterCardTier(previous, f, (byte)tier, low && previous.LowLayerIncluded);
                Check(chained != null && SameBuffer(chained, fresh), $"{name}: chained filter != fresh build at tier {tier}");
                if (tier == 4 || tier == 8) Check(fresh.InstanceCount > 0, $"{name}: tier {tier} is empty");
                // Nesting: every instance of the sparser tier exists unchanged in the denser one.
                Check(Decode(fresh).All(i => denser.Contains(Sig(i))), $"{name}: tier {tier} is not a subset of tier 0");
                previous = fresh;
            }
        }
        RingGrassTileData one = BuildCards(sampler, t, low: false);
        Check(RingGrassTileBuilder.FilterCardTier(one, .5f, 1, true) == null, "FilterCardTier must refuse to resurrect a low layer");
        Check(RingGrassTileBuilder.FilterCardTier(one, .5f, 0, false) == null, "FilterCardTier must refuse a non-downgrade tier");
        // A blade tile is not card data.
        RingGrassTileData blades = RingGrassTileBuilder.Build(t.Key, t.Bounds, 8, 4, 24, 5000, 25, sampler, _clean, t.Geometry, CancellationToken.None);
        Check(RingGrassTileBuilder.FilterCardTier(blades, .5f, 1, false) == null, "FilterCardTier accepted blade data");
    }

    private void TestStratification(RingTerrainSampler sampler, (long a, long b) m)
    {
        var t = Fixture(m.a, m.b);
        double Cv(List<Inst> pts, out double meanNn, out double minNn)
        {
            var nn = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < pts.Count; j++)
                {
                    if (i == j) continue;
                    double da = pts[i].Around - pts[j].Around, dz = pts[i].Across - pts[j].Across;
                    best = Math.Min(best, da * da + dz * dz);
                }
                nn[i] = Math.Sqrt(best);
            }
            double mean = nn.Average(); meanNn = mean; minNn = nn.Min();
            double variance = nn.Select(v => (v - mean) * (v - mean)).Average();
            return Math.Sqrt(variance) / mean;
        }
        RingGrassTileData full = BuildCards(sampler, t, low: false);
        List<Inst> all = Decode(full);
        foreach (float f in new[] { 1f / 8, 1f / 16, 1f / 32 })
        {
            List<Inst> subset = all.Where(i => i.Key < f).ToList();
            if (subset.Count < 60) { _failures.Add($"stratification fixture too sparse at f={f}: {subset.Count}"); continue; }
            double cv = Cv(subset, out double mean, out double min);
            // Control: a hash-random subset of the same eligible set and size (what an unstratified rank would give).
            var rng = new Random(7);
            var control = all.OrderBy(_ => rng.Next()).Take(subset.Count).ToList();
            double controlCv = Cv(control, out double controlMean, out double controlMin);
            GD.Print($"GRASS_CARD[stratification] f=1/{(int)Math.Round(1 / f)} n={subset.Count} nnCV={cv:0.000} mean={mean:0.000} min={min:0.000} | random control nnCV={controlCv:0.000} mean={controlMean:0.000} min={controlMin:0.000}");
            Check(cv < controlCv * .8, $"f={f}: NN CV {cv:0.000} not clearly below the random control {controlCv:0.000}");
            Check(cv < .40, $"f={f}: NN CV {cv:0.000} above the jittered-grid bound .40");
            Check(min > controlMin * 1.5, $"f={f}: min NN {min:0.000} not above the random control {controlMin:0.000} (clumping)");
            // No big holes: empty 8 m cells of the 32 m tile.
            int Empties(List<Inst> set)
            {
                int e = 0;
                for (double a = t.Bounds.AroundMin; a < t.Bounds.AroundMax; a += 8)
                for (double z = t.Bounds.AcrossMin; z < t.Bounds.AcrossMax; z += 8)
                    if (!set.Any(i => i.Around >= a && i.Around < a + 8 && i.Across >= z && i.Across < z + 8)) e++;
                return e;
            }
            int empty = Empties(subset), controlEmpty = Empties(control);
            Check(empty <= controlEmpty, $"f={f}: {empty} empty 8 m cells vs {controlEmpty} for the random control");
        }
    }

    private static int CountIn(RingGrassTileData d, double lo, double hi) => Decode(d).Count(i => i.Around >= lo && i.Around < hi);

    private void TestSeamAndTileInvariance(RingTerrainSampler sampler)
    {
        long lastTile = RingGrassCoordinates.AroundTileCount(Circumference, Tile) - 1;
        long across = 141_000 / 32;
        // Find an across band where land meets the wrap seam on both sides (the seam tiles of random rows are often ocean).
        for (long candidate = 2500; candidate < 9500; candidate += 37)
        {
            if (BuildCards(sampler, Fixture(lastTile, candidate), low: false).InstanceCount > 600 && BuildCards(sampler, Fixture(0, candidate), low: false).InstanceCount > 600) { across = candidate; break; }
        }
        // Lattice cells do not depend on the tile that asks for them: two half-tiles equal the whole tile (set of payloads),
        // at the wrap seam and in the open.
        foreach ((string name, long tileAround) in new[] { ("open", 186_000L / 32), ("seam-last", lastTile), ("seam-first", 0L) })
        {
            RingGrassTileBounds b = RingGrassCoordinates.Bounds(tileAround, across, Tile, Circumference, Width);
            RingGrassTileData Part(double a0, double a1)
            {
                var bounds = new RingGrassTileBounds(a0, a1, b.AcrossMin, b.AcrossMax);
                var geometry = Geometry(new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
                return RingGrassTileBuilder.Build(new RingGrassTileKey(tileAround, across), bounds, 8, 4, 0, 100_000, 25, sampler, _clean, geometry, CancellationToken.None,
                    cards: new RingGrassCardPlacement { IncludeLowLayer = true });
            }
            double mid = (b.AroundMin + b.AroundMax) * .5;
            RingGrassTileData whole = Part(b.AroundMin, b.AroundMax), left = Part(b.AroundMin, mid), right = Part(mid, b.AroundMax);
            var wholeSigs = Decode(whole).Select(Sig).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var halves = Decode(left).Concat(Decode(right)).Select(Sig).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Check(wholeSigs.Count > 200 && wholeSigs.SequenceEqual(halves), $"{name}: split tiles differ from the whole tile ({wholeSigs.Count} vs {halves.Count})");
            if (tileAround == lastTile)
                Check(CountIn(whole, Circumference - 2, Circumference) > 20, "seam-last: no instances in the last 2 m before the wrap");
            if (tileAround == 0)
                Check(CountIn(whole, 0, 2) > 20, "seam-first: no instances in the first 2 m after the wrap");
        }
        // Density continuity across the seam: strips on either side of the wrap carry comparable instance counts.
        RingGrassTileData lastData = BuildCards(sampler, Fixture(lastTile, across), low: false), firstData = BuildCards(sampler, Fixture(0, across), low: false);
        int before = CountIn(lastData, Circumference - 4, Circumference), after = CountIn(firstData, 0, 4);
        GD.Print($"GRASS_CARD[seam] cards in 4 m before wrap={before}, after wrap={after}");
        Check(before > 0 && after > 0 && Math.Abs(before - after) <= Math.Max(60, .6 * Math.Max(before, after)), $"seam density discontinuity {before} vs {after}");
    }

    private static bool Inside(in Inst i, double aMin, double aMax, double zMin, double zMax) => i.Around >= aMin && i.Around < aMax && i.Across >= zMin && i.Across < zMax;

    private void TestExclusionsAndModifiers(RingTerrainSampler sampler, (long a, long b) m)
    {
        var t = Fixture(m.a, m.b);
        double ca = t.Bounds.AroundCenter, cz = t.Bounds.AcrossCenter;
        RingGrassTileData baseline = BuildCards(sampler, t);
        var baseSigs = new HashSet<string>(Decode(baseline).Select(Sig));
        var box = new RingGrassExclusionBox(ca - 5, ca + 5, -1000, 1000, cz - 4, cz + 4);
        var exclusions = new RingGrassExclusionSnapshot(1, Circumference, new[] { box });
        RingGrassTileData excluded = BuildCards(sampler, t, exclusions: exclusions);
        int inBox = Decode(baseline).Count(i => Inside(i, box.AroundMin, box.AroundMax, box.AcrossMin, box.AcrossMax));
        // The card footprint (>= 0.3 m) plus the 0.1 m margin keeps roots clear of the box edge.
        int leaked = Decode(excluded).Count(i => Inside(i, box.AroundMin - .39, box.AroundMax + .39, box.AcrossMin - .39, box.AcrossMax + .39));
        Check(inBox > 100, $"exclusion fixture empty ({inBox})");
        Check(leaked == 0, $"{leaked} cards inside the exclusion box + footprint");
        Check(Decode(excluded).All(i => baseSigs.Contains(Sig(i))), "exclusions changed surviving placements (must only remove)");
        Check(excluded.InstanceCount < baseline.InstanceCount && baseline.InstanceCount - excluded.InstanceCount >= inBox, "exclusion removed too few cards");

        var zeroZone = new RingGrassModifierZone(ca - 8, cz - 8, new Vector2(5, 5), 0, 0, Colors.White, 0);
        var halfZone = new RingGrassModifierZone(ca + 7, cz + 7, new Vector2(7, 7), 0, .5f, Colors.White, 0);
        var mods = new RingGrassModifierSnapshot(1, Circumference, new[] { new RingGrassModifierSnapshot.Entry("zero", zeroZone), new RingGrassModifierSnapshot.Entry("half", halfZone) });
        RingGrassTileData modded = BuildCards(sampler, t, modifiers: mods);
        bool InEllipse(in Inst i, RingGrassModifierZone z) => RingGrassModifierSnapshot.Weight(z, i.Around, i.Across, Circumference) > 0;
        int zeroBase = Decode(baseline).Count(i => InEllipse(i, zeroZone)), zeroAfter = Decode(modded).Count(i => InEllipse(i, zeroZone));
        int halfBase = Decode(baseline).Count(i => InEllipse(i, halfZone)), halfAfter = Decode(modded).Count(i => InEllipse(i, halfZone));
        Check(zeroBase > 100 && zeroAfter == 0, $"zero-density modifier left {zeroAfter} of {zeroBase} cards");
        Check(halfBase > 200 && halfAfter > halfBase * .38 && halfAfter < halfBase * .62, $"half-density modifier kept {halfAfter} of {halfBase}");
        Check(Decode(modded).All(i => baseSigs.Contains(Sig(i))), "modifiers changed surviving placements (must only thin)");
        // Mods and exclusions are filter-nested too.
        RingGrassTileData fresh = BuildCards(sampler, t, .25f, 4, false, exclusions: exclusions, modifiers: mods);
        RingGrassTileData both = BuildCards(sampler, t, exclusions: exclusions, modifiers: mods);
        RingGrassTileData? filtered = RingGrassTileBuilder.FilterCardTier(both, .25f, 4, false);
        Check(filtered != null && SameBuffer(filtered, fresh), "filter != fresh build with exclusions + modifiers");
    }

    private float Presentation(RingTerrainSampler s, long around, int across)
        => s.UsesRuntimeSurfaceGeneration || _clean.UsesFloatSurface ? _clean.GetPresentationSurfaceHeight(s, around, across) : _clean.GetSurfaceHeight(s, around, across);

    private static bool NearCellEdge(in Inst i) => Math.Abs(i.Around - Math.Floor(i.Around) - .5) < .02 || Math.Abs(i.Across - Math.Floor(i.Across) - .5) < .02;

    private void TestSlope(RingTerrainSampler sampler, (long a, long b) m)
    {
        TileFixture best = Fixture(m.a, m.b);
        int bestGap = -1;
        for (int k = 0; k < 24; k++)
        {
            var t = Fixture(m.a + k * 3, m.b + 5 + (k % 4) * 7);
            int gap = BuildCards(sampler, t, low: false, slope: 60).InstanceCount - BuildCards(sampler, t, low: false, slope: 6).InstanceCount;
            if (gap > bestGap) { bestGap = gap; best = t; }
        }
        RingGrassTileData steep = BuildCards(sampler, best, low: false, slope: 60), flat = BuildCards(sampler, best, low: false, slope: 6);
        GD.Print($"GRASS_CARD[slope] cards@60deg={steep.InstanceCount} @6deg={flat.InstanceCount}");
        Check(flat.InstanceCount < steep.InstanceCount, "slope limit did not reduce placement");
        int bad = 0;
        foreach (Inst i in Decode(flat))
        {
            if (NearCellEdge(i)) continue;
            long a = (long)Math.Round(i.Around); int z = (int)Math.Round(i.Across);
            float dx = Presentation(sampler, a + 1, z) - Presentation(sampler, a - 1, z), dz = Presentation(sampler, a, z + 1) - Presentation(sampler, a, z - 1);
            if (Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz) / 2)) > 6.0f) bad++;
        }
        Check(bad == 0, $"{bad} cards sit on terrain steeper than the 6 degree limit");
    }

    private void TestWater(RingTerrainSampler sampler)
    {
        // Find a shoreline: a wet canonical cell with a dry cell within 14 m.
        var rng = new Random(11);
        (long a, int z)? shore = null;
        for (int n = 0; n < 40_000 && shore == null; n++)
        {
            long a = (long)(rng.NextDouble() * Circumference); int z = 20_000 + (int)(rng.NextDouble() * (Width - 40_000));
            TerrainSample s = _clean.GetPresentationTerrainSample(sampler, a, z);
            if (!s.HasSurfaceWater) continue;
            for (int k = 0; k < 8 && shore == null; k++)
            {
                double ang = k * Math.PI / 4;
                long a2 = a + (long)Math.Round(14 * Math.Cos(ang)); int z2 = z + (int)Math.Round(14 * Math.Sin(ang));
                TerrainSample dry = _clean.GetPresentationTerrainSample(sampler, a2, z2);
                if (!dry.HasSurfaceWater && !dry.SiteMask.BlocksVegetation) shore = ((a + a2) / 2, (z + z2) / 2);
            }
        }
        if (shore == null) { _failures.Add("water: no shoreline found for the fixture"); return; }
        // Pick the most mixed wet/dry tile of the 3x3 neighbourhood.
        TileFixture pick = Fixture(shore.Value.a / 32, shore.Value.z / 32); double bestMix = -1;
        for (int da = -1; da <= 1; da++) for (int dz = -1; dz <= 1; dz++)
        {
            var c = Fixture(shore.Value.a / 32 + da, shore.Value.z / 32 + dz);
            int wet = 0, total = 0;
            for (double a = c.Bounds.AroundMin; a < c.Bounds.AroundMax; a += 4) for (double z = c.Bounds.AcrossMin; z < c.Bounds.AcrossMax; z += 4)
            { total++; if (_clean.GetPresentationTerrainSample(sampler, (long)a, (int)z).HasSurfaceWater) wet++; }
            double mix = Math.Min(wet, total - wet);
            if (mix > bestMix) { bestMix = mix; pick = c; }
        }
        Check(bestMix >= 2, $"water fixture has no wet+dry mix (min side {bestMix})");
        RingGrassTileData d = BuildCards(sampler, pick);
        int wetCards = 0;
        foreach (Inst i in Decode(d))
        {
            TerrainSample s = _clean.GetPresentationTerrainSample(sampler, (long)Math.Round(i.Around), (int)Math.Round(i.Across));
            double h = sampler.UsesRuntimeSurfaceGeneration ? _clean.GetPresentationSurfaceHeight(sampler, i.Around, i.Across) : Presentation(sampler, (long)Math.Round(i.Around), (int)Math.Round(i.Across));
            if (RingGrassTileBuilder.IsWaterCovered(s, h)) wetCards++;
        }
        GD.Print($"GRASS_CARD[water] shoreline tile cards={d.InstanceCount} water-covered cards={wetCards} (wet/dry sample min side {bestMix})");
        Check(d.InstanceCount > 0 && wetCards == 0, $"water: {wetCards} of {d.InstanceCount} cards are water covered");
    }

    private void TestHeightByBiome(RingTerrainSampler sampler)
    {
        // Per-instance buckets over many random tiles (the tall/short noise is spatially coherent, so a few tiles are noisy).
        var rng = new Random(23);
        var tiles = new List<(long, long)>();
        for (int n = 0; n < 4000 && tiles.Count < 70; n++)
        {
            long a = (long)(rng.NextDouble() * Circumference); int z = 30_000 + (int)(rng.NextDouble() * (Width - 60_000));
            if (!_clean.GetPresentationTerrainSample(sampler, a, z).HasSurfaceWater) tiles.Add((a / 32, (long)z / 32));
        }
        double[] sum = new double[3]; int[] count = new int[3], tall = new int[3];
        foreach ((long a, long z) in tiles)
            foreach (Inst i in Decode(BuildCards(sampler, Fixture(a, z), low: false)))
            {
                float g = 1 - i.W0 - i.W1 - i.W2 - i.W3, sp = i.W0 + i.W2 + i.W3;
                int bucket = g > .85f ? 0 : i.W1 > .6f ? 1 : sp > .8f ? 2 : -1;
                if (bucket < 0) continue;
                sum[bucket] += i.Height; count[bucket]++; if (i.Height > .45f) tall[bucket]++;
            }
        double Mean(int b) => count[b] == 0 ? 0 : sum[b] / count[b];
        double Tall(int b) => count[b] == 0 ? 0 : tall[b] / (double)count[b];
        GD.Print($"GRASS_CARD[height] over {tiles.Count} tiles: grassland mean={Mean(0):0.000} (n={count[0]}, tall {Tall(0):P0}) forest mean={Mean(1):0.000} (n={count[1]}, tall {Tall(1):P0}) sparse mean={Mean(2):0.000} (n={count[2]}, tall {Tall(2):P0})");
        Check(count[0] > 3000 && count[1] > 1000 && count[2] > 300, $"height fixture counts grass={count[0]} forest={count[1]} sparse={count[2]}");
        Check(Mean(0) > Mean(1) + .02 && Mean(0) > Mean(2) + .05, $"grassland mean height {Mean(0):0.000} not above forest {Mean(1):0.000} / sparse {Mean(2):0.000}");
        Check(Tall(0) > Tall(1) && Tall(1) > Tall(2), $"tall share not ordered grass {Tall(0):P0} > forest {Tall(1):P0} > sparse {Tall(2):P0}");
        Check(Mean(0) > .30 && Mean(0) < .60, $"grassland mean height {Mean(0):0.000} outside the .3-.6 band");
        // The appearance height multipliers still apply on top.
        var tallAppearance = new RingGrassAppearanceSnapshot(true, 1, 1, 1, 1.5f, 1.5f, 1.5f);
        var t = Fixture(tiles[0].Item1, tiles[0].Item2);
        double plain = Decode(BuildCards(sampler, t, low: false)).DefaultIfEmpty().Average(i => i.Height), scaled = Decode(BuildCards(sampler, t, low: false, appearance: tallAppearance)).DefaultIfEmpty().Average(i => i.Height);
        Check(plain == 0 || scaled > plain * 1.2, $"appearance height multiplier ignored ({plain:0.000} -> {scaled:0.000})");
    }

    private void TestSiteMask()
    {
        using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(1337);
        EmitterSiteRegistry registry = v5.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
        TileFixture pick = Fixture(0, 0); int bestBlocked = -1;
        // Tiles straddling the pad edge of a few regular sites (the main pad is 1 km wide), in four directions each.
        for (int index = 0; index < Math.Min(registry.Count - 1, 40) && bestBlocked < 60; index++)
        {
            EmitterSite site = registry[index];
            double padR = registry.Recipe.ShapeFor(site.Kind).PadRadiusM;
            foreach ((int ox, int oz, double scale) in new[] { (1, 0, .6), (1, 0, .8), (1, 0, 1.0), (1, 0, 1.2), (0, 1, .6), (0, 1, .8), (0, 1, 1.0), (0, 1, 1.2), (-1, 0, 1.0), (0, -1, 1.0) })
            {
                long centerA = site.Around + (long)(ox * padR * scale); double centerZ = site.Across + oz * padR * scale;
                if (centerZ < 64 || centerZ > Width - 64) continue;
                var c = Fixture(Math.Max(0, centerA / 32), (long)centerZ / 32);
                int blocked = 0, open = 0;
                for (double a = c.Bounds.AroundMin; a < c.Bounds.AroundMax; a += 2) for (double z = c.Bounds.AcrossMin; z < c.Bounds.AcrossMax; z += 2)
                { if (_clean.GetPresentationTerrainSample(v5, (long)a, (int)z).SiteMask.BlocksVegetation) blocked++; else open++; }
                if (open > 30 && blocked >= 10 && blocked > bestBlocked) { bestBlocked = blocked; pick = c; }
            }
        }
        Check(bestBlocked >= 10, $"site fixture: no tile straddling a pad edge ({bestBlocked})");
        RingGrassTileData d = BuildCards(v5, pick);
        int onSite = 0;
        foreach (Inst i in Decode(d))
        {
            if (NearCellEdge(i)) continue;
            if (_clean.GetPresentationTerrainSample(v5, (long)Math.Round(i.Around), (int)Math.Round(i.Across)).SiteMask.BlocksVegetation) onSite++;
        }
        GD.Print($"GRASS_CARD[site_mask] tile blocked cells(2 m)={bestBlocked} cards={d.InstanceCount} on-site cards={onSite}");
        Check(d.InstanceCount > 0 && onSite == 0, $"{onSite} cards placed on BlocksVegetation terrain");
    }

    private void ReportCostAndCounts(RingTerrainSampler sampler, (long a, long b) m)
    {
        var t = Fixture(m.a, m.b);
        foreach (int tier in new[] { 0, 2, 4, 6, 8, 10, 12, 14 })
        {
            float f = RingGrassCardCurve.LadderFraction(tier);
            RingGrassTileData d = BuildCards(sampler, t, f, (byte)tier, tier <= 3);
            GD.Print($"GRASS_CARD[tier] tier={tier} fraction={f:0.####} cards={d.InstanceCount - d.LowLayerInstanceCount} low={d.LowLayerInstanceCount} bytes={d.Buffer.Length * 4}");
        }
        double Time(Func<RingGrassTileData> build, out int count)
        {
            build(); // warm
            var sw = Stopwatch.StartNew(); RingGrassTileData d = null!; const int runs = 5;
            for (int i = 0; i < runs; i++) d = build();
            count = d.InstanceCount; return sw.Elapsed.TotalMilliseconds / runs;
        }
        double cards0 = Time(() => BuildCards(sampler, t, 1, 0, true), out int n0);
        double cards4 = Time(() => BuildCards(sampler, t, RingGrassCardCurve.LadderFraction(4), 4, false), out int n4);
        double cards8 = Time(() => BuildCards(sampler, t, RingGrassCardCurve.LadderFraction(8), 8, false), out int n8);
        double blades = Time(() => RingGrassTileBuilder.Build(t.Key, t.Bounds, 8, 9, 96, 1700, 25, sampler, _clean, t.Geometry, CancellationToken.None, useGrassClumps: true), out int nb);
        double bladesFar = Time(() => RingGrassTileBuilder.Build(t.Key, t.Bounds, 8, 9, 96, 1700, 25, sampler, _clean, t.Geometry, CancellationToken.None, sourceFraction: .09f, sourceTier: 2, useGrassClumps: true), out int nbf);
        GD.Print($"GRASS_CARD[cost] 32 m tile build ms: cards tier0+low={cards0:0.0} ({n0}), cards tier4={cards4:0.0} ({n4}), cards tier8={cards8:0.0} ({n8}) | blades 96/m2 clump near={blades:0.0} ({nb}), blades far tier={bladesFar:0.0} ({nbf})");
    }

    // ------------------------------------------------------------------ blade golden

    internal static List<string> BladeGoldenLines(RingTerrainSampler sampler)
    {
        var lines = new List<string>();
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var key = new RingGrassTileKey(186_000 / 48, 141_000 / 48);
        RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(key.Around, key.Across, 48, Circumference, Width);
        RingLodGeometrySnapshot geometry = Geometry(new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
        var exclusions = new RingGrassExclusionSnapshot(1, Circumference, new[] { new RingGrassExclusionBox(bounds.AroundMin + 10, bounds.AroundMin + 20, -1000, 1000, bounds.AcrossMin + 5, bounds.AcrossMin + 25) });
        var modifiers = new RingGrassModifierSnapshot(1, Circumference, new[] { new RingGrassModifierSnapshot.Entry("m", new RingGrassModifierZone(bounds.AroundCenter, bounds.AcrossCenter, new Vector2(14, 14), 4, .4f, Colors.White, 0)) });
        foreach (bool dense in new[] { false, true })
        foreach (float fraction in new[] { 1f, .49f, .09f })
        foreach (bool extras in new[] { false, true })
        {
            float density = dense ? 24 : 0;
            RingGrassTileData d = RingGrassTileBuilder.Build(key, bounds, 8, 9, density, 100_000, 25, sampler, clean, geometry, CancellationToken.None,
                exclusions: extras ? exclusions : null, modifiers: extras ? modifiers : null,
                sourceFraction: fraction, sourceTier: (byte)(fraction >= 1 ? 0 : fraction > .3f ? 1 : 2), useGrassClumps: dense);
            // A capped build pins the bounded-priority truncation path.
            RingGrassTileData c = RingGrassTileBuilder.Build(key, bounds, 8, 9, density, 300, 25, sampler, clean, geometry, CancellationToken.None,
                sourceFraction: fraction, useGrassClumps: dense);
            lines.Add($"{(dense ? "dense" : "legacy")}/f{fraction:0.##}/{(extras ? "mods" : "plain")} 0x{d.PlacementHash:X16} n={d.InstanceCount} cap300=0x{c.PlacementHash:X16}/{c.InstanceCount}");
        }
        return lines;
    }
}
