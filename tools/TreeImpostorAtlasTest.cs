using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7a gate: the card-atlas bake is deterministic (bake twice, save twice, same bytes), matches the committed resource and manifest,
/// covers the LOD2 silhouette on every card, keeps mips 0 to 3 of the selected normals within 3 % of full resolution under sun-only noon / 30 / 8 degrees,
/// and a stale source hash is detected. Deliberately broken inputs must be rejected by the same checks.</summary>
public partial class TreeImpostorAtlasTest : RefCounted
{
    private const double SumTolerance = .03;
    private const float MipTolerance = .03f;

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static string Check(TreeImpostorAtlasBaker.Input source, TreeImpostorAtlasBaker.VariantBake bake)
    {
        var rows = TreeImpostorAtlasBaker.SilhouetteCheck(source, bake);
        for (int c = 0; c < rows.Length; c++)
        {
            if (rows[c].SilhouetteSum <= 0) { if (rows[c].BakedSum > 0) return $"card {c} baked coverage {rows[c].BakedSum:F1} where the silhouette is empty"; continue; }
            double relative = Math.Abs(rows[c].BakedSum - rows[c].SilhouetteSum) / rows[c].SilhouetteSum;
            if (relative > SumTolerance) return $"card {c} coverage sum off by {relative:P1}";
            // The IoU must beat the baked half-coverage mask's IoU against itself moved one texel: below that it is sampling noise, not a different shape.
            if (rows[c].Iou <= rows[c].ShiftIou) return $"card {c} silhouette IoU {rows[c].Iou:F3} not above the one-texel shift IoU {rows[c].ShiftIou:F3}";
        }
        return "";
    }

    public int Run()
    {
        try
        {
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            string root = ProjectSettings.GlobalizePath("res://");
            var bakes = PrepareTreeImpostorAtlas.BakeAll(variants, out var inputs);

            // layout: every variant has cards, exactly one horizontal (top) card, an affine UV fit within one texel, and the atlas stays 256 per layer
            int cards = 0;
            for (int i = 0; i < variants.Count; i++)
            {
                var b = bakes[i];
                Require(b.CardCount >= 3, $"v{i} {variants[i].Name} has {b.CardCount} cards");
                Require(b.TopCards == 1, $"v{i} {variants[i].Name} has {b.TopCards} horizontal cards (expected exactly one)");
                // The pack's side cards have three vertex rows, and in four pine variants (v19, v22, v23, v27) the middle row's v is not midway between the outer
                // rows, so their UVs are piecewise affine. One affine map then misses the pack UVs by up to 0.93 texel (v22), about 0.5 % of the tree height and
                // under half a pixel at the 325 m hand-off; every other card fits to 1e-5. The limit is one atlas texel: a larger miss means a wrong card grouping.
                double fitTexels = b.WorstFitResidual * PreparedTreeImpostorAtlas.LayerSize;
                Require(fitTexels < 1.0, $"v{i} UV-to-position fit misses the pack UVs by {fitTexels:F2} texels (limit 1)");
                Require(b.CoveredTexels > 200, $"v{i} covers only {b.CoveredTexels} texels");
                Require(b.Albedo.Length == PreparedTreeImpostorAtlas.AlbedoLayerBytes && b.NormalMips.Length == PreparedTreeImpostorAtlas.NormalLayerBytes, $"v{i} layer size");
                cards += b.CardCount;
                for (int t = 0; t < PreparedTreeImpostorAtlas.LayerSize * PreparedTreeImpostorAtlas.LayerSize; t++)
                    if (b.Albedo[t * 4 + 3] > 0)
                    {
                        float x = b.NormalMips[t * 4] / 255f * 2 - 1, y = b.NormalMips[t * 4 + 1] / 255f * 2 - 1, z = b.NormalMips[t * 4 + 2] / 255f * 2 - 1;
                        Require(MathF.Abs(MathF.Sqrt(x * x + y * y + z * z) - 1) < .03f, $"v{i} texel {t} normal is not unit length");
                    }
            }

            // coverage of every baked card against the LOD2 silhouette from that card's view
            for (int i = 0; i < variants.Count; i++)
            {
                string failure = Check(inputs[i], bakes[i]);
                Require(failure == "", $"v{i} {variants[i].Name}: {failure}");
            }
            // it must fail on a deliberately broken input: the same bake made without the foliage, checked against the real silhouette
            {
                int probe = 3;
                var broken = TreeImpostorAtlasBaker.Bake(inputs[probe].Without(s => s.Cutout));
                Require(Check(inputs[probe], broken) != "", "coverage check accepted a bake made without the foliage");
            }

            // mips 0 to 3: sun-only response within 3 % of full resolution
            string table = PrepareTreeImpostorAtlas.MipTable(variants, bakes, out float worstMip);
            string scratch = ProjectSettings.GlobalizePath("res://.godot/tr7a");
            Directory.CreateDirectory(scratch);
            File.WriteAllText(scratch + "/mip_table_test.md", table);
            Require(worstMip <= MipTolerance, $"normal mip response differs from full resolution by {worstMip:P2} (limit {MipTolerance:P0}); see .godot/tr7a/mip_table_test.md");
            // averaging the same normals (a plain box filter) must not be what we ship: selected texels are surface normals, checked above as unit length at every level
            for (int i = 0; i < variants.Count; i++)
                for (int level = 1; level <= 3; level++)
                {
                    int size = PreparedTreeImpostorAtlas.LevelSize(level), offset = PreparedTreeImpostorAtlas.NormalLevelOffsetBytes(level);
                    for (int t = 0; t < size * size; t++)
                        if (bakes[i].Coverage[level][t] > 0)
                        {
                            float x = bakes[i].NormalMips[offset + t * 4] / 255f * 2 - 1, y = bakes[i].NormalMips[offset + t * 4 + 1] / 255f * 2 - 1, z = bakes[i].NormalMips[offset + t * 4 + 2] / 255f * 2 - 1;
                            Require(MathF.Abs(MathF.Sqrt(x * x + y * y + z * z) - 1) < .03f, $"v{i} mip {level} texel {t} is not a surface normal");
                        }
                }

            // determinism: bake again, compare the arrays; save both resources and compare the files byte for byte
            var again = PrepareTreeImpostorAtlas.BakeAll(variants, out _);
            for (int i = 0; i < variants.Count; i++)
                Require(bakes[i].Albedo.AsSpan().SequenceEqual(again[i].Albedo) && bakes[i].NormalMips.AsSpan().SequenceEqual(again[i].NormalMips), $"v{i} bake is not deterministic");
            PreparedTreeImpostorAtlas first = PrepareTreeImpostorAtlas.Pack(variants, bakes), second = PrepareTreeImpostorAtlas.Pack(variants, again);
            string pa = "res://.godot/tr7a/det_a.res", pb = "res://.godot/tr7a/det_b.res";
            Require(ResourceSaver.Save(first, pa, PrepareTreeImpostorAtlas.SaveFlags) == Error.Ok && ResourceSaver.Save(second, pb, PrepareTreeImpostorAtlas.SaveFlags) == Error.Ok, "save determinism files");
            byte[] ba = File.ReadAllBytes(ProjectSettings.GlobalizePath(pa)), bb = File.ReadAllBytes(ProjectSettings.GlobalizePath(pb));
            Require(ba.AsSpan().SequenceEqual(bb), $"saved resources differ byte for byte (sizes {ba.Length}/{bb.Length})");

            // committed resource: bytes equal a fresh bake's save, hashes fresh, manifest hash matches the file
            string committed = root + "resources/trees/prepared/lolipop_impostor_atlas.res";
            Require(File.Exists(committed), "lolipop_impostor_atlas.res is missing; run prepare_tree_impostor_atlas");
            byte[] stored = File.ReadAllBytes(committed);
            Require(stored.AsSpan().SequenceEqual(ba), "committed lolipop_impostor_atlas.res differs from a fresh bake; rerun prepare_tree_impostor_atlas");
            using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(root + "resources/trees/prepared/lolipop_impostor_atlas.manifest.json")))
            {
                Require(manifest.RootElement.GetProperty("artifact_sha256").GetString() == Convert.ToHexString(SHA256.HashData(stored)), "manifest artifact hash is stale");
                var listed = manifest.RootElement.GetProperty("variants").EnumerateArray().ToArray();
                Require(listed.Length == variants.Count, "manifest variant count");
                for (int i = 0; i < listed.Length; i++) Require(listed[i].GetProperty("source_hash").GetString() == first.SourceHashes[i], $"manifest source hash v{i}");
            }
            var loaded = ResourceLoader.Load<PreparedTreeImpostorAtlas>(ImportedTreeImpostorAtlasBake.ResourcePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                ?? throw new InvalidOperationException("lolipop_impostor_atlas.res did not load");
            Require(ImportedTreeImpostorAtlasBake.IsFresh(loaded, variants, out int staleCount, out string reason) && staleCount == 0, "committed resource is stale: " + reason);

            // stale detection: a changed source hash, a missing resource and a different variant list are all reported
            var stale = (PreparedTreeImpostorAtlas)loaded.Duplicate(true);
            stale.SourceHashes = stale.SourceHashes.Select(h => new string('0', h.Length)).ToArray();
            Require(!ImportedTreeImpostorAtlasBake.IsFresh(stale, variants, out staleCount, out _) && staleCount == variants.Count, "stale hashes were not detected");
            var oneStale = (PreparedTreeImpostorAtlas)loaded.Duplicate(true);
            oneStale.SourceHashes[5] = "deadbeef";
            Require(!ImportedTreeImpostorAtlasBake.IsFresh(oneStale, variants, out staleCount, out _) && staleCount == 1, "a single stale hash was not detected");
            Require(!ImportedTreeImpostorAtlasBake.IsFresh(null, variants, out _, out _), "a missing resource counted as fresh");
            Require(!ImportedTreeImpostorAtlasBake.IsFresh(loaded, variants.Take(variants.Count - 1).ToList(), out _, out _), "a different variant list counted as fresh");

            foreach (string p in new[] { pa, pb }) File.Delete(ProjectSettings.GlobalizePath(p));
            GD.Print($"TREE_IMPOSTOR_ATLAS_TEST=PASS variants={variants.Count} cards={cards} worst_mip_difference={worstMip:P2} bytes={stored.Length}");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("TREE_IMPOSTOR_ATLAS_TEST=FAIL " + exception.Message);
            return 1;
        }
    }
}
