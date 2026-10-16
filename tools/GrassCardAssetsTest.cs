using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.GrassCardsSpike;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Headless contract test for the production grass-card asset factories (S2): atlas determinism/coverage/channels,
/// card mesh contract, cache behaviour and parity with the P0 spike. Run via tools/grass_card_assets_test.gd.</summary>
public partial class GrassCardAssetsTest : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);

    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        try { EmitSignal(SignalName.Completed, Run()); }
        catch (Exception exception) { GD.PushError("GRASS_CARD_ASSETS=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private void Require(bool ok, string message) { if (!ok) _failures.Add(message); }
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private bool Run()
    {
        RingGrassCardAssets.ClearForTests();
        RingGrassCardAtlasParameters d = RingGrassCardAtlasParameters.VariantD;
        Require(d == new RingGrassCardAtlasParameters(56, 1337, .16f, .24f, 1.2f), "variant D parameters changed");

        // Determinism: main thread twice plus two concurrent worker-thread builds.
        var a = RingGrassCardAtlasGenerator.Build(d);
        var b = RingGrassCardAtlasGenerator.Build(d);
        var tasks = new[] { Task.Run(() => RingGrassCardAtlasGenerator.Build(d)), Task.Run(() => RingGrassCardAtlasGenerator.Build(d)) };
        Task.WaitAll(tasks);
        Require(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "atlas not identical across two in-process builds");
        foreach (var t in tasks) Require(t.Result.Pixels.AsSpan().SequenceEqual(a.Pixels) && t.Result.FullHash == a.FullHash, "atlas differs on a worker thread");
        Require(a.Width == 2048 && a.Height == 512 && a.MipCount == 12, $"atlas dims/mips {a.Width}x{a.Height} mips={a.MipCount}");
        Require(a.Pixels.Length == Enumerable.Range(0, 12).Sum(m => Math.Max(1, 2048 >> m) * Math.Max(1, 512 >> m) * 4), "atlas chain byte length");
        GD.Print($"GRASS_CARD_ASSETS atlas mip0Hash={a.Mip0Hash} fullHash={a.FullHash:X16} cacheKey={d.CacheKey():X16} outlineWaste={F(a.OutlineWasteFraction * 100)}% buildMs={F(a.BuildMilliseconds)}");
        Require(d.CacheKey() != (d with { Seed = 1338 }).CacheKey() && d.CacheKey() != (d with { BladesPerVariant = 57 }).CacheKey()
            && d.CacheKey() != (d with { WidthMultiplier = 1.21f }).CacheKey() && d.CacheKey() == new RingGrassCardAtlasParameters(56, 1337, .16f, .24f, 1.2f).CacheKey(), "cache key sensitivity");

        // Channel contract (mip 0): R mask 0 outside blades, G gradient 0..1 increasing with height, B per blade random, A 128.
        int maskPixels = 0, badAlpha = 0, gOutOfOrder = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
        {
            if (a.Pixels[i * 4 + 3] != 128) badAlpha++;
            if (a.Pixels[i * 4] >= 128) maskPixels++;
        }
        // Gradient rises with height: mean G of solid mask pixels in the top quarter exceeds the bottom quarter, per variant.
        for (int v = 0; v < 4; v++)
        {
            double top = 0, bottom = 0; int tn = 0, bn = 0;
            for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++)
                {
                    int i = (y * 2048 + v * 512 + x) * 4;
                    if (a.Pixels[i] < 128) continue;
                    if (y < 128) { top += a.Pixels[i + 1]; tn++; } else if (y >= 384) { bottom += a.Pixels[i + 1]; bn++; }
                }
            if (tn == 0 || bn == 0 || top / tn <= bottom / bn + 40) gOutOfOrder++;
            // B must vary between blades (several distinct values among mask pixels).
            var distinctB = new HashSet<byte>();
            for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) { int i = (y * 2048 + v * 512 + x) * 4; if (a.Pixels[i] >= 128) distinctB.Add(a.Pixels[i + 2]); }
            Require(distinctB.Count >= 20, $"variant {v}: B channel has only {distinctB.Count} distinct blade values");
        }
        Require(badAlpha == 0, "alpha channel not constant 128"); Require(gOutOfOrder == 0, "G height gradient contract failed"); Require(maskPixels > 20000, "atlas mask nearly empty");
        // Mask is zero outside the card outline.
        int outside = 0;
        for (int v = 0; v < 4; v++)
            for (int y = 0; y < 512; y++)
            {
                float hw = RingGrassCardAtlasGenerator.OutlineHalfWidth(1f - (y + .5f) / 512f);
                for (int x = 0; x < 512; x++) if (MathF.Abs((x + .5f) / 512f - .5f) > hw && a.Pixels[(y * 2048 + v * 512 + x) * 4] != 0) outside++;
            }
        Require(outside == 0, $"{outside} mask pixels outside the card outline");

        // Coverage per mip (down to mip 6) vs mip 0 at the 0.5 cutoff.
        var covLine = new System.Text.StringBuilder();
        for (int m = 1; m <= 6; m++)
        {
            double worstPts = 0; int tilePixels = Math.Max(1, (2048 >> m) / 4) * Math.Max(1, 512 >> m);
            for (int v = 0; v < 4; v++)
            {
                double c0 = a.Coverage(0, v), cm = a.Coverage(m, v);
                worstPts = Math.Max(worstPts, Math.Abs(cm - c0) * 100.0);
                // Allowed: 1 percentage point, or two pixels of quantisation at coarse mips.
                Require(Math.Abs(cm - c0) <= Math.Max(0.01, 2.0 / tilePixels) + 1e-6, $"mip{m} v{v} coverage {F(cm * 100)}% vs mip0 {F(c0 * 100)}%");
            }
            covLine.Append($" mip{m}:max|d|={F(worstPts)}pt(px={tilePixels})");
        }
        GD.Print("GRASS_CARD_ASSETS coverage" + covLine);

        // Card mesh contract.
        var cards = RingGrassCardMeshFactory.Build();
        Require(cards.Vertices.Length == 18 && cards.TriangleCount == 12 && cards.Indices.Length == 36, $"card mesh counts v={cards.Vertices.Length} t={cards.TriangleCount}");
        Require(cards.Indices.All(i => i >= 0 && i < 18), "card indices out of range");
        var uv2 = cards.Uv2;
        for (int k = 0; k < 3; k++)
        {
            for (int j = 0; j < 6; j++)
            {
                Require(uv2[k * 6 + j].Y == k, $"card {k} vertex {j} UV2.y != {k}");
                Require(MathF.Abs(uv2[k * 6 + j].X) > .2f, "UV2.x is not a card-local offset");
            }
            // Card angle: right edge of the widest row points along (cos a, 0, -sin a).
            Vector3 edge = cards.Vertices[k * 6 + 3] - cards.Vertices[k * 6 + 2];
            float ang = MathF.Atan2(-edge.Z, edge.X);
            Require(MathF.Abs(ang - k * Mathf.Pi / 3f) < 1e-4f, $"card {k} angle {ang}");
            Require(MathF.Abs(cards.Vertices[k * 6].Y) < 1e-6f && MathF.Abs(cards.Vertices[k * 6 + 1].Y) < 1e-6f, $"card {k} root not pinned at y=0");
            Require(MathF.Abs(cards.Vertices[k * 6 + 4].Y - 1f) < 1e-6f, $"card {k} top row not at y=1");
        }
        Aabb ab = cards.CustomAabb;
        bool contains = true; foreach (Vector3 v in cards.Vertices) contains &= ab.HasPoint(v);
        Require(contains && ab.Size.X >= 0.46f * 1.6f * 2.5f * 2f && ab.Size.Y >= 1f * 2.5f, "card AABB does not cover collapse gain * compensation cap");
        var cardMesh = cards.GetMesh();
        Require(cardMesh.GetSurfaceCount() == 1 && ReferenceEquals(cardMesh, cards.GetMesh()), "card ArrayMesh not stable");
        Require(cardMesh.GetFaces().Length == 36, "card mesh face count");

        // Cache: hit returns the same resource, parameter change invalidates, timings.
        RingGrassCardAssets.ClearForTests();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s1 = RingGrassCardAssets.GetBlocking(d);
        double coldMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s2 = RingGrassCardAssets.GetBlocking(d);
        double hitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Require(ReferenceEquals(s1, s2) && RingGrassCardAssets.BuildCount >= 1, "cache hit returned a different set");
        int builds = RingGrassCardAssets.BuildCount;
        Require(RingGrassCardAssets.TryGet(d, out var polled) && ReferenceEquals(polled, s1), "TryGet did not return the cached set");
        Require(ReferenceEquals(s1.AtlasTexture, s2.AtlasTexture) && ReferenceEquals(s1.CardArrayMesh, s2.CardArrayMesh), "cache hit returned different Godot resources");
        Require(s1.AtlasTexture.GetWidth() == 2048 && s1.AtlasTexture.GetHeight() == 512, "atlas texture size");
        Require(RingGrassCardAssets.BuildCount == builds, "resource access rebuilt data");
        var other = d with { Seed = 99 };
        Require(!RingGrassCardAssets.TryGet(other, out _), "uncached parameters reported ready");
        var s3 = RingGrassCardAssets.GetBlocking(other);
        Require(!ReferenceEquals(s3, s1) && s3.Atlas.FullHash != s1.Atlas.FullHash && RingGrassCardAssets.BuildCount == builds + 1, "parameter change did not invalidate the cache");
        Require(s1.Atlas.FullHash == a.FullHash, "cached atlas differs from a direct build");
        GD.Print($"GRASS_CARD_ASSETS timing coldBuild={F(coldMs)}ms (atlas={F(s1.Atlas.BuildMilliseconds)}ms set={F(s1.BuildMilliseconds)}ms) cacheHit={F(hitMs)}ms altParams set={F(s3.BuildMilliseconds)}ms");

        // Parity with the P0 spike.
        var spike = new GrassCardsSpikeAssets();
        ImageTexture spikeTex = spike.BuildAtlasFull(56, 1337, 0.16f, 0.24f, 1.2f);
        byte[] spikePixels = spikeTex.GetImage().GetData();
        // Mip0Hash intentionally differs from the spike now: uncovered mip-0 texels carry push-pull filled G/B (covered texels are checked below).
        // R (coverage) must match the spike on every mip; G/B only on mip-0 covered texels (G/B are now coverage weighted across mips).
        Require(spikePixels.Length == a.Pixels.Length, "atlas pixel length differs from the spike");
        {
            int mw = a.Width, mh = a.Height, off = 0, mips = 0;
            var sbMean = new List<string>();
            double m0G = 0, m0B = 0;
            while (true)
            {
                int n = mw * mh;
                double sumG = 0, sumB = 0; int cnt = 0, rDiff = 0, gbDiff = 0;
                for (int i = 0; i < n; i++)
                {
                    int o = off + i * 4;
                    if (spikePixels[o] != a.Pixels[o]) rDiff++;
                    if (a.Pixels[o] >= 128) { sumG += a.Pixels[o + 1] / 255.0; sumB += a.Pixels[o + 2] / 255.0; cnt++; }
                    if (mips == 0 && a.Pixels[o] > 0 && (spikePixels[o + 1] != a.Pixels[o + 1] || spikePixels[o + 2] != a.Pixels[o + 2])) gbDiff++;
                }
                Require(rDiff == 0, $"mip {mips} R differs from the spike on {rDiff} texels");
                Require(gbDiff == 0, $"mip 0 G/B differ from the spike on {gbDiff} covered texels");
                double mg = cnt > 0 ? sumG / cnt : 0, mb = cnt > 0 ? sumB / cnt : 0;
                if (mips == 0) { m0G = mg; m0B = mb; }
                else if (cnt > 0)
                {
                    // Mips 1-4 (near/mid cards) must hold G within .03. From mip 5 the quantile-rescaled R >= 128 selection favours dense
                    // root texels (low G), so the mean G legitimately drops; only guard against a return to box-filter dilution there.
                    bool gOk = mips <= 4 ? Math.Abs(mg - m0G) <= .03 : mg >= m0G * .72;
                    Require(gOk && Math.Abs(mb - m0B) <= .03, $"mip {mips} G/B diluted: meanG={mg:0.000} meanB={mb:0.000} vs mip0 {m0G:0.000}/{m0B:0.000}");
                    sbMean.Add($"mip{mips}:G={mg:0.000},B={mb:0.000}");
                }
                else sbMean.Add($"mip{mips}:no-R>=128");
                off += n * 4; mips++;
                if (mw == 1 && mh == 1) break;
                mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2);
            }
            Require(mips == a.MipCount, "mip walk count differs from the atlas mip count");
            GD.Print($"GRASS_CARD_ASSETS antiDilution mip0:G={m0G:0.000},B={m0B:0.000} " + string.Join(" ", sbMean));
        }
        Require(MathF.Abs(spike.LastOutlineWasteFraction - a.OutlineWasteFraction) < 1e-7f, "outline waste differs from the spike");
        ArrayMesh spikeCards = spike.BuildMesh();
        var sa = spikeCards.SurfaceGetArrays(0);
        Require(sa[(int)Mesh.ArrayType.Vertex].AsVector3Array().SequenceEqual(cards.Vertices) && sa[(int)Mesh.ArrayType.TexUV].AsVector2Array().SequenceEqual(cards.Uv)
            && sa[(int)Mesh.ArrayType.TexUV2].AsVector2Array().SequenceEqual(cards.Uv2) && sa[(int)Mesh.ArrayType.Index].AsInt32Array().SequenceEqual(cards.Indices), "card mesh differs from the spike");
        GD.Print($"GRASS_CARD_ASSETS parity atlasR=identical atlasPixels=({a.Pixels.Length}B) cardMesh=identical");

        if (_failures.Count > 0) { foreach (string f in _failures) GD.PushError("GRASS_CARD_ASSETS fail: " + f); GD.Print("GRASS_CARD_ASSETS=FAIL " + _failures.Count); return false; }
        GD.Print("GRASS_CARD_ASSETS=PASS");
        return true;
    }
}
