using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-9 (docs/work-orders/terrain-vegetation/T6.md, design B1-B4, B6): ground-cover card kinds (flower, fern, tall grass) chosen by community in worlds with vegetation.ground_cover (bit 7), and grass density
/// and height that follow the coarse habitat fields. Budget neutrality (equal instance counts and positions with and without the substitution), kinds only where their community has weight, the monotone
/// canopy and wetness responses, the feature off byte-identical to the generation 1 golden at the eight fingerprint tiles, no kind 2 or reed in any buffer, the deterministic atlas, and the build cost.
/// Prints GROUND_COVER_CARDS=PASS. Headless: tools/run_tests.ps1 -Tests ground_cover_cards_test. Optional user arg sheet=&lt;png&gt; writes the atlas sheet.
/// </summary>
public partial class GroundCoverCardsTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const float Tile = 32f;
    private const string FeatureId = "vegetation.ground_cover";
    private const int Stride = RingGrassTileData.FloatsPerInstance;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("GROUND_COVER_CARDS_DETAIL " + text);
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct TileKey(string Pose, long A, long Z);

    private RingTerrainSampler _on = null!, _off = null!;
    private RingGrassCardPlacement _placement = null!;
    private int _mainCap;
    private readonly List<TileKey> _tiles = new();
    private readonly RingLodSurfaceEditSnapshot _clean = new(1, Circ, new Dictionary<RingTerrainColumnCoordinate, int>());

    public int Run(string sheetPath = "")
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Setup();
            CommunityTable();
            Atlas(sheetPath);
            Detail($"atlas checks done at {clock.Elapsed.TotalSeconds:F1} s");
            Tiles();
            Detail($"tile checks done at {clock.Elapsed.TotalSeconds:F1} s");
            BuildCost();
        }
        catch (Exception e) { Console.Error.WriteLine("GROUND_COVER_CARDS=FAIL exception " + e); return 1; }
        finally { _on?.Dispose(); _off?.Dispose(); RingGrassTileBuilder.GroundCoverProbeForTests = null; }
        if (_failures.Count != 0) { Console.Error.WriteLine("GROUND_COVER_CARDS=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("GROUND_COVER_CARDS=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ setup

    private void Setup()
    {
        LandscapeWorld.Spec specOn = LandscapeWorld.For(1, new[] { FeatureId }), specOff = LandscapeWorld.For(1);
        _on = specOn.CreateSampler();
        _off = specOff.CreateSampler();
        Check((_on.WorldFeatureMask & (1 << 7)) != 0 && (_off.WorldFeatureMask & (1 << 7)) == 0, "bit 7 is set in the review world and clear in generation 1");
        Check(WorldGenerationLedger.Production.Features.Any(f => f.Id == FeatureId && f.Bit == 7 && f.Plan == "T6" && f.FrozenIn == 0), "the ledger has no open vegetation.ground_cover at bit 7");
        var tuning = RingGrassCardTuning.Default;
        RingGrassStyleProfile style = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(RingStandingGrassLayer.GrassRenderStyleKind.Cards, 0, Tile, 400f, 0, 256, 0, tuning));
        _placement = style.PlacementForTier(0);
        _mainCap = style.MainCapPerTile;
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapeDressingPoses.FixturePath));
        foreach (string name in WorldGenerationFingerprint.TilePoses) AddTile(t0.Poses.First(p => p.Name == name));
        foreach (string name in LandscapeDressingPoses.ExpectedNames) AddTile(dressing.Poses.First(p => p.Name == name));
        Detail($"worlds: on mask={_on.WorldFeatureMask} off mask={_off.WorldFeatureMask}; {_tiles.Count} tiles; main cap {_mainCap}");
    }

    private void AddTile(LandscapePose p) => _tiles.Add(new TileKey(p.Name, (long)Math.Floor(p.Around / Tile), (long)Math.Floor(p.Across / Tile)));

    private RingGrassTileData BuildTile(RingTerrainSampler sampler, TileKey tile, float share = 1f)
    {
        RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(tile.A, tile.Z, Tile, Circ, Width);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circ / Math.Tau, 0, 0, Circ, -1000, 1000,
            new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
        return RingGrassTileBuilder.Build(new RingGrassTileKey(tile.A, tile.Z), bounds, 8f, 9, 0f, _mainCap, 25f, sampler, _clean, geometry, System.Threading.CancellationToken.None,
            exclusions: null, appearance: RingGrassAppearanceSnapshot.Default, sourceFraction: 1f, sourceTier: 0, useGrassClumps: false, modifiers: null,
            modifierDensityStrength: 1f, meadowTufts: false, meadowSpread: 1f, minimumHeight: .10f, maximumHeight: .20f, useConfiguredHeightRange: true, cards: _placement, groundCoverShare: share);
    }

    private static int KindOf(float[] buffer, int index) => (int)Math.Floor(Math.Floor(buffer[index * Stride + 16]) / 4.0);

    private static string HashHex(float[] buffer)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (float f in buffer)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(f);
            for (int i = 0; i < 4; i++) { h ^= (byte)(bits >> (8 * i)); h *= 0x100000001B3UL; }
        }
        return "0x" + h.ToString("X16", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ community table (B3, A5)

    private void CommunityTable()
    {
        // Every share row's kind split and variant split sum to 1; grassland stands in where no community holds.
        var none = new RingCommunityMemberships(0, 0, 0, 0, 0, 0, 0, 0);
        RingPlantCommunities.KindSplit(none, out float f0, out float fl0, out float t0);
        Check(Math.Abs(f0 + fl0 + t0 - 1f) < 1e-5f && Math.Abs(RingPlantCommunities.CoverShare(none) - 0.10f) < 1e-6f && f0 == 0f, "no community must give the grassland row (share 0.10, no ferns)");
        for (int c = 0; c < RingCommunityMemberships.Count; c++)
        {
            var arr = new float[8]; arr[c] = 1f;
            var pure = new RingCommunityMemberships(arr[0], arr[1], arr[2], arr[3], arr[4], arr[5], arr[6], arr[7]);
            RingPlantCommunities.KindSplit(pure, out float fe, out float fo, out float ta);
            RingPlantCommunities.VariantWeights(pure, out float w, out float y, out float v, out float p);
            Check(Math.Abs(fe + fo + ta - 1f) < 1e-5f && Math.Abs(w + y + v + p - 1f) < 1e-5f, $"row {c} splits do not sum to 1");
            Check(Math.Abs(RingPlantCommunities.CoverShare(pure) - RingPlantCommunities.RowCoverShare(c)) < 1e-6f, $"row {c} pure share differs from the table");
        }
        // A5: a dense, moist forest is closed broadleaf; an open treeless wet hollow is wet meadow; a high canopy of conifers is a conifer stand.
        RingCommunityMemberships closed = RingPlantCommunities.Memberships(0.8f, 0f, 0.1f, 0.5f, 0.2f, 0f, 0.7f, 0f);
        Check(closed.ClosedBroadleaf > 0.85f && closed.ConiferStand < 0.15f && closed.WetMeadow == 0f, "a dense forest is not closed broadleaf");
        RingCommunityMemberships wet = RingPlantCommunities.Memberships(0f, 0f, 0f, 0.9f, 0f, 0f, 0.8f, 0f);
        Check(wet.WetMeadow == 1f && wet.Grassland == 0f, "a treeless wet hollow is not wet meadow");
        Check(RingPlantCommunities.Memberships(0.8f, 0f, 0.9f, 0.5f, 0.2f, 0f, 0.7f, 0f).ConiferStand > 0.85f, "a dense conifer forest is not a conifer stand");
        Detail("community table: rows sum to 1, pure rows give the B3 shares, A5 anchors hold");
    }

    // ------------------------------------------------------------------ atlas (B2)

    private void Atlas(string sheetPath)
    {
        RingGroundCoverAssets.ClearForTests();
        var p = RingGroundCoverAtlasParameters.Default;
        RingGroundCoverAtlasData a = RingGroundCoverAtlasGenerator.Build(p);
        RingGroundCoverAtlasData b = RingGroundCoverAtlasGenerator.Build(p);
        var worker = Task.Run(() => RingGroundCoverAtlasGenerator.Build(p)).GetAwaiter().GetResult();
        RingGroundCoverAtlasData cached = RingGroundCoverAssets.GetBlocking(p);
        bool same = a.FullHash == b.FullHash && a.FullHash == worker.FullHash && a.FullHash == cached.FullHash;
        for (int l = 0; l < a.Layers && same; l++) same = a.LayerPixels[l].AsSpan().SequenceEqual(b.LayerPixels[l]) && a.LayerPixels[l].AsSpan().SequenceEqual(worker.LayerPixels[l]);
        Check(same, "e. the ground-cover atlas differs between two builds, a worker build and the cache");
        Check(RingGroundCoverAssets.GetBlocking(p) == cached && RingGroundCoverAssets.BuildCount == 1, "e. the cache built the atlas more than once");
        Check(RingGroundCoverAtlasGenerator.GeneratorVersion == 1 && p.CacheKey() != new RingGroundCoverAtlasParameters(1338).CacheKey()
            && p.CacheKey() != RingGrassCardAtlasParameters.VariantD.CacheKey() && RingGrassCardAtlasGenerator.GeneratorVersion == 2, "e. cache key or generator versions");
        int expected = Enumerable.Range(0, a.MipCount).Sum(m => Math.Max(1, 1024 >> m) * Math.Max(1, 512 >> m) * 4);
        Check(a.Layers == 3 && a.Width == 1024 && a.Height == 512 && a.MipCount == 11 && a.LayerPixels.All(l => l.Length == expected), $"e. atlas dims {a.Layers} layers {a.Width}x{a.Height} mips {a.MipCount}");
        Detail($"e. atlas layer hashes {string.Join(" ", a.LayerHashes.Select(h => h.ToString("X16", CultureInfo.InvariantCulture)))} full {a.FullHash:X16} build {F(a.BuildMilliseconds)} ms");
        // Pinned layer hashes: a later reed layer must leave these three unchanged (design B2); a change here is a generator version bump.
        ulong[] pinned = { 0x1FEF588575E7D562UL, 0x53001849D8AE5ED6UL, 0x4ED02C69141D55C0UL };
        for (int l = 0; l < 3; l++) Check(a.LayerHashes[l] == pinned[l], $"e. layer {l} hash {a.LayerHashes[l]:X16} differs from the pinned {pinned[l]:X16}");
        // Channels: coverage per variant at mip 0 is sensible and preserved down the chain; A holds petals (flowers), seed heads (tall grass v1-3) and nothing on ferns and the sedge.
        for (int l = 0; l < 3; l++)
            for (int v = 0; v < 4; v++)
            {
                float cov0 = Coverage(a, l, 0, v);
                Check(cov0 > 0.02f && cov0 < 0.6f, $"e. layer {l} variant {v} mip 0 coverage {F(cov0 * 100)} %");
                for (int m = 1; m <= 5; m++)
                {
                    float c = Coverage(a, l, m, v);
                    Check(Math.Abs(c - cov0) <= (m <= 3 ? 0.15f : 0.30f) * cov0 + 0.005f, $"e. layer {l} variant {v} mip {m} coverage {F(c * 100)} % vs {F(cov0 * 100)} % at mip 0");
                }
                float maskA = MaskFraction(a, l, v);
                bool expectMask = l == 0 || (l == 2 && v > 0);
                Check(expectMask ? maskA > 0.0015f : maskA == 0f, $"e. layer {l} variant {v} A mask fraction {F(maskA * 100)} % (expected {(expectMask ? "some" : "none")})");
            }
        // Flower heads stay small against the leaves: of the covered texels of each flower variant at mip 0, the leaf and stem share (A below 128) is at least 60 % and the petal share at most 40 %.
        for (int v = 0; v < 4; v++)
        {
            int covered = 0, petal = 0; byte[] fp = a.LayerPixels[0];
            for (int y = 0; y < 512; y++) for (int x = 0; x < 256; x++) { int i = (y * 1024 + v * 256 + x) * 4; if (fp[i] >= 128) { covered++; if (fp[i + 3] >= 128) petal++; } }
            float share = petal / (float)Math.Max(1, covered);
            Detail($"e. flower variant {v}: petal share of covered texels {F(share * 100)} %, leaf share {F((1 - share) * 100)} %");
            Check(share <= 0.40f, $"e. flower variant {v} petal share {F(share * 100)} % of its covered texels exceeds 40 %");
        }
        // A is an area mean: the far mip of a flower layer keeps a petal share well below its mip 0 peak but above zero.
        Detail("e. coverage kept within 15 % over mips 1-3 and 30 % over mips 4-5; A masks: flowers all variants, tall grass variants 1-3, none on ferns and the sedge");
        if (sheetPath.Length > 0) WriteSheet(a, sheetPath);
    }

    private static float Coverage(RingGroundCoverAtlasData a, int layer, int mip, int variant)
    {
        int offset = 0, lw = a.Width, lh = a.Height;
        for (int m = 0; m < mip; m++) { offset += lw * lh * 4; lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2); }
        int tileW = Math.Max(1, lw / 4), hit = 0;
        byte[] px = a.LayerPixels[layer];
        for (int y = 0; y < lh; y++) for (int x = 0; x < tileW; x++) if (px[offset + (y * lw + variant * tileW + x) * 4] >= 128) hit++;
        return hit / (float)(tileW * lh);
    }

    private static float MaskFraction(RingGroundCoverAtlasData a, int layer, int variant)
    {
        int hit = 0; byte[] px = a.LayerPixels[layer];
        for (int y = 0; y < 512; y++) for (int x = 0; x < 256; x++) if (px[(y * 1024 + variant * 256 + x) * 4 + 3] >= 128) hit++;
        return hit / (256f * 512f);
    }

    /// <summary>Mip 0 of the three layers as the shader colours them: the terrain-ish green leaf (R over a dark ground) with the A mask shown as petal colour (flowers), straw (tall grass).</summary>
    private static void WriteSheet(RingGroundCoverAtlasData a, string path)
    {
        const int w = 1024, h = 512, gap = 8;
        int sheetW = w, sheetH = 3 * h + 2 * gap;
        var bytes = new byte[sheetW * sheetH * 4];
        Color[] petals = { new(.70f, .70f, .64f), new(.72f, .55f, .06f), new(.24f, .20f, .50f), new(.55f, .18f, .40f) };
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 40; bytes[i + 1] = 44; bytes[i + 2] = 40; bytes[i + 3] = 255; }
        for (int layer = 0; layer < 3; layer++)
        {
            ReadOnlySpan<byte> px = a.Mip0(layer);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4, o = (((layer * (h + gap)) + y) * sheetW + x) * 4;
                    float cov = px[i] / 255f, g = px[i + 1] / 255f, aMask = px[i + 3] / 255f;
                    if (cov < 0.5f) { float edge = cov * 0.4f; bytes[o] = (byte)(40 + edge * 60); bytes[o + 1] = (byte)(44 + edge * 80); bytes[o + 2] = (byte)(40 + edge * 40); continue; }
                    Color leaf = new(0.18f + 0.22f * g, 0.34f + 0.30f * g, 0.12f + 0.10f * g);
                    if (layer == 1) leaf = new(leaf.R * 0.85f, leaf.G, leaf.B * 0.8f);
                    Color target = layer == 0 ? petals[Math.Min(3, x / 256)] : layer == 2 ? new Color(.75f, .68f, .45f) : leaf;
                    Color c = leaf.Lerp(target, aMask);
                    bytes[o] = (byte)(Math.Clamp(c.R, 0f, 1f) * 255f); bytes[o + 1] = (byte)(Math.Clamp(c.G, 0f, 1f) * 255f); bytes[o + 2] = (byte)(Math.Clamp(c.B, 0f, 1f) * 255f);
                }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Error error = Image.CreateFromData(sheetW, sheetH, false, Image.Format.Rgba8, bytes).SavePng(path);
        Detail($"atlas sheet {path} ({error})");
    }

    // ------------------------------------------------------------------ tiles

    private sealed class Bin
    {
        public long Candidates, Placed, Before, After;
        public double HeightSum, RatioSum, RatioSquares; public long HeightCount;
    }

    /// <summary>One tile's probe records: b. every cover card has a community of weight above 0.05 behind its kind; the canopy bins count grass candidates; the wetness bins collect grass card heights.</summary>
    private void Accumulate(List<RingGrassTileBuilder.GroundCoverProbe> records, string label, Bin[] canopyBins, Bin[] wetBins)
    {
        foreach (var r in records)
        {
            if (r.LatticeKind != RingGrassCardKind.Card)
            {
                Check(r.Kind == r.LatticeKind, $"b. {label}: the low layer emitted {r.Kind}");
                continue;
            }
            int cb = Math.Min(4, (int)(r.Fields.Canopy * 5f));
            if (!r.Placed)
            {
                canopyBins[cb].Candidates++;
                if (r.ChanceBefore >= .02f && r.Accept <= r.ChanceBefore) canopyBins[cb].Before++;
                if (r.ChanceAfter >= .02f && r.Accept <= r.ChanceAfter) canopyBins[cb].After++;
                Check(r.ChanceAfter <= r.ChanceBefore + 1e-6f, $"c. {label}: the density response raised a chance");
                continue;
            }
            canopyBins[cb].Placed++;
            if (r.Kind == RingGrassCardKind.Card)
            {
                int wb = Math.Min(4, (int)(r.Fields.Wetness * 5f));
                double ratio = r.BaseHeight > 0f ? r.Height / r.BaseHeight : 1.0;
                wetBins[wb].HeightSum += r.Height; wetBins[wb].RatioSum += ratio; wetBins[wb].RatioSquares += ratio * ratio; wetBins[wb].HeightCount++;
            }
            int column = r.Kind == RingGrassCardKind.Fern ? RingPlantCommunities.FernKind : r.Kind == RingGrassCardKind.Flower ? RingPlantCommunities.FlowerKind : r.Kind == RingGrassCardKind.TallGrass ? RingPlantCommunities.TallKind : -1;
            if (column >= 0)
                Check(RingPlantCommunities.KindSupport(r.Fields.Memberships, column) > RingPlantCommunities.MinimumWeight, $"b. {label}: {r.Kind} at ({F(r.Around)}, {F(r.Across)}) has no community above 0.05 behind it (best {F(RingPlantCommunities.KindSupport(r.Fields.Memberships, column))})");
        }
    }

    private void Tiles()
    {
        string path = ProjectSettings.GlobalizePath(WorldGenerationFingerprintTest.GoldenPath);
        JsonElement golden = WorldGenerationFingerprintTest.ReadRows(path)[1];
        var goldenGrass = golden.GetProperty("grass").EnumerateArray().ToDictionary(e => e.GetProperty("pose").GetString()!, e => (Hash: e.GetProperty("hash").GetString()!, Count: e.GetProperty("count").GetInt32()));

        var canopyBins = Enumerable.Range(0, 5).Select(_ => new Bin()).ToArray();
        var wetBins = Enumerable.Range(0, 5).Select(_ => new Bin()).ToArray();
        long cover = 0, mainTotal = 0, identical = 0, differing = 0, kindFlowers = 0, kindFerns = 0, kindTall = 0;
        var probeRecords = new List<RingGrassTileBuilder.GroundCoverProbe>();
        int matchedGolden = 0, fingerprintTiles = 0;
        foreach (TileKey tile in _tiles)
        {
            // d. feature off: byte-identical to the current builder (the golden grass hash) at the eight fingerprint tiles.
            RingGrassTileData off = BuildTile(_off, tile);
            bool isFingerprint = WorldGenerationFingerprint.TilePoses.Contains(tile.Pose);
            if (isFingerprint)
            {
                fingerprintTiles++;
                bool match = goldenGrass.TryGetValue(tile.Pose, out var g) && g.Hash == HashHex(off.Buffer) && g.Count == off.InstanceCount;
                Check(match, $"d. {tile.Pose}: feature-off grass hash {HashHex(off.Buffer)} ({off.InstanceCount}) differs from the golden");
                if (match) matchedGolden++;
            }
            for (int i = 0; i < off.InstanceCount; i++) { int k = KindOf(off.Buffer, i); Check(k == 0 || k == 1, $"d. {tile.Pose}: feature-off kind {k}"); }

            // a. budget neutrality in the review world: share 1 against share 0 (positions and counts equal, only kinds and sizes change).
            RingGrassTileBuilder.GroundCoverProbeForTests = probeRecords.Add;
            probeRecords.Clear();
            RingGrassTileData on = BuildTile(_on, tile, 1f);
            RingGrassTileBuilder.GroundCoverProbeForTests = null;
            RingGrassTileData none = BuildTile(_on, tile, 0f);
            Check(on.InstanceCount == none.InstanceCount && on.LowLayerInstanceCount == none.LowLayerInstanceCount, $"a. {tile.Pose}: instances {on.InstanceCount} (low {on.LowLayerInstanceCount}) with cover, {none.InstanceCount} ({none.LowLayerInstanceCount}) without");
            int main = on.InstanceCount - on.LowLayerInstanceCount;
            bool samePositions = on.InstanceCount == none.InstanceCount;
            for (int i = 0; i < Math.Min(on.InstanceCount, none.InstanceCount) && samePositions; i++)
                samePositions = on.Buffer[i * Stride + 3] == none.Buffer[i * Stride + 3] && on.Buffer[i * Stride + 7] == none.Buffer[i * Stride + 7] && on.Buffer[i * Stride + 11] == none.Buffer[i * Stride + 11];
            Check(samePositions, $"a. {tile.Pose}: a card moved between the substitution on and off");
            if (on.Buffer.AsSpan().SequenceEqual(off.Buffer)) identical++; else differing++;

            var flowerVariants = new int[4];
            // f. kinds: the low layer is never a cover kind, the main cards are 0 or 3-5, nothing is 2 or 6, and without the share nothing is a cover kind.
            for (int i = 0; i < on.InstanceCount; i++)
            {
                int k = KindOf(on.Buffer, i);
                bool low = i >= main;
                Check(k != 2 && k != 6 && (low ? k == 1 : k == 0 || (k >= 3 && k <= 5)), $"f. {tile.Pose}: instance {i} has kind {k} ({(low ? "low layer" : "main")})");
                if (!low) { mainTotal++; if (k == 3) { cover++; kindFlowers++; flowerVariants[(int)Math.Floor(on.Buffer[i * Stride + 16]) & 3]++; } else if (k == 4) { cover++; kindFerns++; } else if (k == 5) { cover++; kindTall++; } }
            }
            for (int i = 0; i < none.InstanceCount; i++) { int k = KindOf(none.Buffer, i); Check(k == 0 || k == 1, $"f. {tile.Pose}: share 0 emitted kind {k}"); }
            Detail($"a. {tile.Pose}: flower variants white/yellow/violet/pink {flowerVariants[0]}/{flowerVariants[1]}/{flowerVariants[2]}/{flowerVariants[3]}");
            // B6: a lower share turns a hash prefix of the cover cards back into grass (the kinds of the survivors are unchanged).
            RingGrassTileData half = BuildTile(_on, tile, 0.5f);
            bool subset = half.InstanceCount == on.InstanceCount;
            int halfCover = 0, fullCover = 0;
            for (int i = 0; i < Math.Min(half.InstanceCount, on.InstanceCount); i++)
            {
                int kh = KindOf(half.Buffer, i), kf = KindOf(on.Buffer, i);
                if (kh >= 3) { halfCover++; subset &= kh == kf && half.Buffer[i * Stride + 16] == on.Buffer[i * Stride + 16]; }
                if (kf >= 3) fullCover++;
            }
            Check(subset && halfCover <= fullCover, $"B6. {tile.Pose}: the share 0.5 cover set is not a subset of the share 1 set ({halfCover} of {fullCover})");
            if (fullCover >= 40) Check(halfCover > fullCover * 0.3 && halfCover < fullCover * 0.7, $"B6. {tile.Pose}: share 0.5 kept {halfCover} of {fullCover} cover cards");

            // b. cover kinds only where their community has weight; c. the canopy and wetness responses.
            Accumulate(probeRecords, tile.Pose, canopyBins, wetBins);
        }
        // The stand blocks (the tile of each forest, plains and lowland pose and its eight neighbours) add candidates to the bins, which the 14 tiles fill thinly at high canopy.
        foreach (string pose in new[] { "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye", "plains_eye", "lowland_eye" })
        {
            TileKey centre = _tiles.First(t => t.Pose == pose);
            for (long da = -1; da <= 1; da++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (da == 0 && dz == 0) continue;
                    RingGrassTileBuilder.GroundCoverProbeForTests = probeRecords.Add;
                    probeRecords.Clear();
                    BuildTile(_on, new TileKey(pose + "+", centre.A + da, centre.Z + dz), 1f);
                    RingGrassTileBuilder.GroundCoverProbeForTests = null;
                    Accumulate(probeRecords, pose + "+", canopyBins, wetBins);
                }
        }
        Check(matchedGolden == 8 && fingerprintTiles == 8, $"d. {matchedGolden} of {fingerprintTiles} fingerprint tiles equal the golden grass hash");
        Check(cover > 0 && differing > 0, "a. the review world has no cover card on any tile");
        Detail($"a. {_tiles.Count} tiles: counts and positions equal with and without cover; {cover} of {mainTotal} main cards are cover ({kindFlowers} flowers, {kindFerns} ferns, {kindTall} tall grass); {differing} tiles differ from generation 1, {identical} identical");
        Detail($"d. {matchedGolden} of 8 fingerprint tiles equal the golden grass hash with the feature off; f. no kind 2 or 6, the low layer is kind 1, share 0 gives no cover kind");

        // c. canopy: cards per candidate against the same candidates without the response (the accept hash against the chance after and before the density factor) is non-increasing with canopy;
        // wetness: the mean ratio of a grass card's height to its height before the field factor is non-decreasing. One standard error of tolerance.
        var lines = new List<string>();
        for (int i = 0; i < 5; i++)
            lines.Add($"canopy bin {i}: {canopyBins[i].After}/{canopyBins[i].Before} = {F(canopyBins[i].Before > 0 ? canopyBins[i].After / (double)canopyBins[i].Before : 0)} ({canopyBins[i].Candidates} candidates, {canopyBins[i].Placed} placed)");
        Detail("c. " + string.Join("; ", lines));
        double? previousRate = null, previousSe = null; int usedCanopy = 0;
        for (int i = 0; i < 5; i++)
        {
            if (canopyBins[i].Before < 300) continue;
            double rate = canopyBins[i].After / (double)canopyBins[i].Before, se = Math.Sqrt(rate * (1 - rate) / canopyBins[i].Before);
            if (previousRate.HasValue) Check(rate <= previousRate.Value + previousSe!.Value + se, $"c. canopy bin {i} keeps {F(rate)} of its grass, above the lower bin's {F(previousRate.Value)} by more than one standard error");
            previousRate = rate; previousSe = se; usedCanopy++;
        }
        Check(usedCanopy >= 3, $"c. only {usedCanopy} canopy bins hold 300 grass candidates");
        Check(canopyBins[0].Before > 300 && canopyBins[0].After / (double)canopyBins[0].Before > 0.9, "c. open ground (canopy below 0.2) loses grass");
        lines.Clear();
        for (int i = 0; i < 5; i++)
            lines.Add($"wetness bin {i}: n={wetBins[i].HeightCount} ratio {F(wetBins[i].HeightCount > 0 ? wetBins[i].RatioSum / wetBins[i].HeightCount : 0)} mean height {F(wetBins[i].HeightCount > 0 ? wetBins[i].HeightSum / wetBins[i].HeightCount : 0)} m");
        Detail("c. " + string.Join("; ", lines));
        double? previousMean = null, previousError = null; int usedWet = 0;
        for (int i = 0; i < 5; i++)
        {
            Bin b = wetBins[i];
            if (b.HeightCount < 200) continue;
            double mean = b.RatioSum / b.HeightCount, sd = Math.Sqrt(Math.Max(0, b.RatioSquares / b.HeightCount - mean * mean)), se = sd / Math.Sqrt(b.HeightCount);
            if (previousMean.HasValue) Check(mean >= previousMean.Value - previousError!.Value - se, $"c. wetness bin {i} height ratio {F(mean)} is below the lower bin's {F(previousMean.Value)} by more than one standard error");
            previousMean = mean; previousError = se; usedWet++;
        }
        Check(usedWet >= 2, $"c. only {usedWet} wetness bins hold 200 grass cards");

        // The patch is pure per position: two builds agree, and the fields are continuous across a tile edge.
        TileKey first = _tiles[3];
        RingGrassTileBounds b0 = RingGrassCoordinates.Bounds(first.A, first.Z, Tile, Circ, Width), b1 = RingGrassCoordinates.Bounds(first.A + 1, first.Z, Tile, Circ, Width);
        var pa = RingCoarseHabitatPatch.TryCreate(_on, _clean, b0.AroundMin, b0.AroundMax, b0.AcrossMin, b0.AcrossMax);
        var pb = RingCoarseHabitatPatch.TryCreate(_on, _clean, b1.AroundMin, b1.AroundMax, b1.AcrossMin, b1.AcrossMax);
        var pa2 = RingCoarseHabitatPatch.TryCreate(_on, _clean, b0.AroundMin, b0.AroundMax, b0.AcrossMin, b0.AcrossMax);
        Check(pa != null && pb != null && pa2 != null, "the coarse patch could not be created");
        if (pa != null && pb != null && pa2 != null)
        {
            double edge = b0.AroundMax, across = b0.AcrossMin + 13.7;
            RingCoverFields x = pa.Sample(edge - 1e-6, across), y = pb.Sample(edge, across), z = pa2.Sample(edge - 1e-6, across);
            Check(Math.Abs(x.Canopy - y.Canopy) < 1e-3f && Math.Abs(x.Wetness - y.Wetness) < 1e-3f && Math.Abs(x.Memberships.ClosedBroadleaf - y.Memberships.ClosedBroadleaf) < 1e-3f, "the coarse fields jump across a tile edge");
            Check(x.Canopy == z.Canopy && x.Wetness == z.Wetness && x.Memberships.Sum == z.Memberships.Sum, "the coarse patch is not deterministic");
        }
    }

    // ------------------------------------------------------------------ cost (g)

    private void BuildCost()
    {
        // A first build of each tile on a fresh independent sampler (cold node caches, as a worker's tile build is), best of three passes, after a throwaway JIT warm-up.
        var offTimes = new double[_tiles.Count]; var onTimes = new double[_tiles.Count]; var zeroTimes = new double[_tiles.Count];
        Array.Fill(offTimes, double.MaxValue); Array.Fill(onTimes, double.MaxValue); Array.Fill(zeroTimes, double.MaxValue);
        for (int pass = -1; pass < 3; pass++)
        {
            using RingTerrainSampler offCopy = _off.CreateIndependentCopy(), onCopy = _on.CreateIndependentCopy(), zeroCopy = _on.CreateIndependentCopy();
            offCopy.EnablePresentationLatticeCache(); onCopy.EnablePresentationLatticeCache(); zeroCopy.EnablePresentationLatticeCache();
            for (int t = 0; t < _tiles.Count; t++)
            {
                TileKey tile = pass < 0 ? new TileKey("warmup", _tiles[t].A + 5000, _tiles[t].Z) : _tiles[t];
                var sw = Stopwatch.StartNew(); BuildTile(offCopy, tile); double off = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); BuildTile(onCopy, tile); double on = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); BuildTile(zeroCopy, tile, 0f); double zero = sw.Elapsed.TotalMilliseconds;
                if (pass >= 0) { offTimes[t] = Math.Min(offTimes[t], off); onTimes[t] = Math.Min(onTimes[t], on); zeroTimes[t] = Math.Min(zeroTimes[t], zero); }
            }
        }
        double offMedian = Median(offTimes), onMedian = Median(onTimes);
        Detail($"g. with the share at 0 (the field read and density response, no substitution): median {F(Median(zeroTimes))} ms");
        // Where the on-minus-off time goes: the patch creation alone (the coarse nodes, their taps and the 81 derived nodes), cold on a fresh copy.
        using (RingTerrainSampler patchCopy = _on.CreateIndependentCopy())
        {
            patchCopy.EnablePresentationLatticeCache();
            var patchTimes = new List<double>();
            foreach (TileKey t in _tiles)
            {
                RingGrassTileBounds pb = RingGrassCoordinates.Bounds(t.A, t.Z, Tile, Circ, Width);
                var sw2 = Stopwatch.StartNew();
                RingCoarseHabitatPatch.TryCreate(patchCopy, _clean, pb.AroundMin, pb.AroundMax, pb.AcrossMin, pb.AcrossMax);
                patchTimes.Add(sw2.Elapsed.TotalMilliseconds);
            }
            Detail($"g. patch creation alone: median {F(Median(patchTimes.ToArray()))} ms, max {F(patchTimes.Max())} ms (cold, first call of each tile on one copy)");
        }
        double ratio = offMedian > 0 ? onMedian / offMedian : 1;
        Detail($"g. grass tile build median {F(offMedian)} ms feature off, {F(onMedian)} ms on ({F((ratio - 1) * 100)} %) over {_tiles.Count} tiles, best of 3 cold passes");
        for (int t = 0; t < _tiles.Count; t++) Detail($"g. {_tiles[t].Pose}: off {F(offTimes[t])} ms, on {F(onTimes[t])} ms");
        if (offMedian >= 1.0) Check(ratio <= 1.20, $"g. the grass tile build median rose {F((ratio - 1) * 100)} % ({F(offMedian)} to {F(onMedian)} ms), above the +20 % gate");
        else Detail("g. the off median is below 1 ms: reported only");
    }

    private static double Median(double[] values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * 0.5;
    }
}
