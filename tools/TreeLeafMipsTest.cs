using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7c-1 gate (headless): the stored lolipop_leaf_mips.res against a fresh build (the per-level table of passing area against level 0 within 0.5 points; hash equality is reported, not gated),
/// level 0 equal to the import, the table gates on the stored levels, the loader's swap, stale and missing fall back, and a control: with the importer's own mips the same gates must fail.</summary>
public partial class TreeLeafMipsTest : RefCounted
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public int Run()
    {
        try
        {
            ImportedTreeLeafMipCatalog.ConfigureForDiagnostics(null, false); // the catalog hands out the import's textures; the swap is exercised below
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            Require(ImportedTreeCatalog.LoadedFrom == "prepared", "the catalog must load prepared, it loaded from " + ImportedTreeCatalog.LoadedFrom);
            var catalog = ResourceLoader.Load<PreparedImportedTreeCatalog>(ImportedTreeCatalog.PreparedResourcePath);
            Require(ResourceLoader.Exists(ImportedTreeLeafMipCatalog.ResourcePath), "lolipop_leaf_mips.res is missing (run tools/run_tests.ps1 -Tests prepare_tree_leaf_mips)");
            var stored = ResourceLoader.Load<PreparedTreeLeafMips>(ImportedTreeLeafMipCatalog.ResourcePath, null, ResourceLoader.CacheMode.Ignore);
            Require(ImportedTreeLeafMipCatalog.Validate(stored) != null && stored != null, "the stored resource is unreadable or unsupported");
            Require(stored.CatalogFingerprint == catalog.SourceFingerprint, "the stored resource was built for another catalog fingerprint");

            var failures = new List<string>();
            List<PrepareTreeLeafMips.Atlas> atlases = PrepareTreeLeafMips.Group(variants, failures);
            Require(failures.Count == 0, "atlas grouping: " + string.Join("; ", failures));
            Require(atlases.Count == 3 && stored.AtlasNames.Length == 3, "expected 3 leaf atlases");
            foreach (PrepareTreeLeafMips.Atlas atlas in atlases)
            {
                int i = Array.IndexOf(stored.AtlasNames, atlas.Name);
                Require(i >= 0, $"{atlas.Name}: not in the stored resource");
                Require(stored.SourcePaths[i] == atlas.Path && stored.Cutoffs[i] == atlas.Cutoff, $"{atlas.Name}: recorded source or cutoff differs");
                Image import = atlas.Texture.GetImage(), mips = stored.Textures[i].GetImage();
                int size = import.GetWidth();
                Require(stored.Sizes[i] == size && mips.GetWidth() == size && mips.GetHeight() == size, $"{atlas.Name}: size");
                Require(mips.GetFormat() == import.GetFormat() && mips.GetMipmapCount() == import.GetMipmapCount() && mips.GetData().Length == import.GetData().Length, $"{atlas.Name}: format, mip count or data length differ from the import");
                int level0 = (int)import.GetMipmapOffset(1);
                Require(import.GetData().AsSpan(0, level0).SequenceEqual(mips.GetData().AsSpan(0, level0)), $"{atlas.Name}: level 0 differs from the import");

                byte[] rgba = PrepareTreeLeafMips.DecodeLevel0(import);
                byte[][] storedAlpha = PrepareTreeLeafMips.DecodeChain(mips, size, out byte[][] storedRgb);
                string freshHash = PrepareTreeLeafMips.BuildAndDecode(atlas, rgba, size, out byte[][] freshAlpha, out byte[][] freshRgb);
                var storedRatios = PrepareTreeLeafMips.LevelRatios(atlas, rgba, size, storedAlpha);
                var freshRatios = PrepareTreeLeafMips.LevelRatios(atlas, rgba, size, freshAlpha);
                var importRatios = PrepareTreeLeafMips.LevelRatios(atlas, rgba, size, PrepareTreeLeafMips.DecodeChain(import, size));
                var storedMisses = new List<string>(); var importMisses = new List<string>();
                double worstDifference = 0; int identical = 0, compared = 0;
                for (int l = 1; l < storedRatios.Length; l++)
                {
                    compared++;
                    if (storedAlpha[l].AsSpan().SequenceEqual(freshAlpha[l])) identical++;
                    if (storedRatios[l] == null) continue;
                    int s = size >> l;
                    var stored1 = storedRatios[l].Values.ToList();
                    storedMisses.AddRange(PrepareTreeLeafMips.GateLevel(atlas.Name, s, stored1).Select(m => $"stored L{l}: {m}"));
                    importMisses.AddRange(PrepareTreeLeafMips.GateLevel(atlas.Name, s, importRatios[l].Values.ToList()));
                    double difference = Math.Abs(stored1.Average() - freshRatios[l].Values.Average());
                    worstDifference = Math.Max(worstDifference, difference);
                    Require(difference <= 0.005, $"{atlas.Name} L{l} ({s}x{s}): stored species mean {stored1.Average():0.000} differs from a fresh build's {freshRatios[l].Values.Average():0.000} by more than 0.5 points");
                }
                // colour: the stored levels against a fresh build (table within 0.5 points) and the colour gates on the stored resource
                double[]?[] storedColour = PrepareTreeLeafMips.ColourLevels(atlas, rgba, size, storedAlpha, storedRgb, out _);
                double[]?[] freshColour = PrepareTreeLeafMips.ColourLevels(atlas, rgba, size, freshAlpha, freshRgb, out _);
                double worstColour = 0;
                for (int l = 1; l < storedColour.Length; l++)
                {
                    Require((storedColour[l] == null) == (freshColour[l] == null), $"{atlas.Name} L{l}: the stored and the fresh build disagree on whether anything passes");
                    if (storedColour[l] == null) continue;
                    double difference = Math.Abs(storedColour[l]![0] - freshColour[l]![0]);
                    worstColour = Math.Max(worstColour, difference);
                    Require(difference <= 0.005, $"{atlas.Name} L{l} ({size >> l}x{size >> l}): stored colour luminance {storedColour[l]![0]:0.000} differs from a fresh build's {freshColour[l]![0]:0.000} by more than 0.5 points");
                }
                List<string> colourMisses = PrepareTreeLeafMips.GateColourLevels(atlas.Name, size, storedColour);
                Require(colourMisses.Count == 0, $"{atlas.Name}: the stored levels miss the colour gates: {string.Join("; ", colourMisses.Take(5))}");
                Require(storedMisses.Count == 0, $"{atlas.Name}: the stored levels miss the table gates: {string.Join("; ", storedMisses.Take(5))}");
                Require(importMisses.Count > 0, $"{atlas.Name}: the importer's own mips pass the table gates, so the test cannot see the feature missing");
                GD.Print($"TREE_LEAF_MIPS_TEST {atlas.Name}: level 0 equals the import, stored vs fresh worst species-mean difference {worstDifference * 100:0.00} points, colour worst difference {worstColour * 100:0.00} points, decoded levels byte-identical {identical}/{compared}, fresh build hash {freshHash[..12]}; the importer's mips miss {importMisses.Count} gates (control)");
            }

            // The loader: stale and missing fall back, one warning per cause, and the materials keep the import.
            int leafMaterials = Apply(variants, stored, catalog.SourceFingerprint + "x", out string reason, out int warnings);
            Require(leafMaterials == 0 && reason.Contains("fingerprint") && warnings == 1, "a stale fingerprint must fall back with one warning: " + reason);
            var staleCutoff = Clone(stored); staleCutoff.Cutoffs[0] += 0.01f;
            int partial = Apply(variants, staleCutoff, catalog.SourceFingerprint, out reason, out warnings);
            Require(partial > 0 && reason.Contains("cutoff") && warnings == 1, "a stale cutoff must skip only that atlas, with one warning: " + reason);
            ImportedTreeLeafMipCatalog.SetActive(false);
            var staleSize = Clone(stored); staleSize.Sizes[1] /= 2;
            int partialSize = Apply(variants, staleSize, catalog.SourceFingerprint, out reason, out warnings);
            Require(partialSize > 0 && reason.Contains("size") && warnings == 1, "a stale size must skip only that atlas, with one warning: " + reason);
            ImportedTreeLeafMipCatalog.SetActive(false);
            ImportedTreeLeafMipCatalog.ConfigureForDiagnostics("res://resources/trees/prepared/lolipop_leaf_mips_does_not_exist.res", true);
            Require(ImportedTreeLeafMipCatalog.Load() == null && ImportedTreeLeafMipCatalog.FallbackReason.Contains("missing") && ImportedTreeLeafMipCatalog.WarningCount == 1, "a missing resource must fall back with one warning");

            // The good swap and the control switch.
            int swapped = Apply(variants, stored, catalog.SourceFingerprint, out reason, out warnings);
            Require(swapped > 0 && warnings == 0, "the stored resource must swap the leaf materials without a warning: " + reason);
            foreach (ShaderMaterial m in LeafMaterials(variants))
            {
                Require(m.GetShaderParameter("leaf_alpha_isotropic").AsBool(), "a leaf material is not set to the isotropic alpha read");
                var texture = (Texture2D)m.GetShaderParameter("albedo_texture").AsGodotObject();
                Require(texture.GetInstanceId() == m.GetShaderParameter("leaf_alpha_texture").AsGodotObject().GetInstanceId(), "albedo and alpha textures differ");
                float expectedLod = (float)(Math.Log2(texture.GetWidth()) - 2);
                Require(Math.Abs(m.GetShaderParameter("leaf_alpha_max_lod").AsSingle() - expectedLod) < 1e-4f, $"leaf_alpha_max_lod {m.GetShaderParameter("leaf_alpha_max_lod").AsSingle()} is not log2(size) - 2 = {expectedLod}");
                Require(!stored.SourcePaths.Contains(texture.ResourcePath), "a leaf material still holds the import");
            }
            // The impostor atlas source hash reads level 0 only: the same with the stable mips on and off.
            var hashesOn = variants.Select(ImportedTreeImpostorAtlasBake.HashSources).ToArray();
            Require(!ImportedTreeLeafMipCatalog.SetActive(false), "SetActive(false) must report the stable mips off");
            var hashesOff = variants.Select(ImportedTreeImpostorAtlasBake.HashSources).ToArray();
            for (int v = 0; v < variants.Count; v++) Require(hashesOn[v] == hashesOff[v], $"variant {v}: HashSources differs between stable mips on and off");
            foreach (ShaderMaterial m in LeafMaterials(variants))
                Require(!m.GetShaderParameter("leaf_alpha_isotropic").AsBool() && stored.SourcePaths.Contains(((Texture2D)m.GetShaderParameter("albedo_texture").AsGodotObject()).ResourcePath), "the control did not put the import back");
            Require(ImportedTreeLeafMipCatalog.SetActive(true), "SetActive(true) must report the stable mips on");
            foreach (ShaderMaterial m in LeafMaterials(variants))
                Require(m.GetShaderParameter("leaf_alpha_max_lod").AsSingle() > 0f && m.GetShaderParameter("leaf_alpha_max_lod").AsSingle() < 16f, "SetActive(true) must restore leaf_alpha_max_lod");
            GD.Print($"TREE_LEAF_MIPS_TEST swap: {swapped} leaf materials; stale fingerprint, cutoff and size fall back with one warning each; missing resource falls back");
            GD.Print("TREE_LEAF_MIPS_TEST=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("TREE_LEAF_MIPS_TEST=FAIL " + exception.Message);
            GD.Print("TREE_LEAF_MIPS_TEST=FAIL " + exception);
            return 1;
        }
    }

    /// <summary>Apply on a pristine state: the materials are put back first, so every call starts from the import.</summary>
    private static int Apply(IReadOnlyList<ImportedTreeVariant> variants, PreparedTreeLeafMips resource, string fingerprint, out string reason, out int warnings)
    {
        ImportedTreeLeafMipCatalog.SetActive(false);
        ImportedTreeLeafMipCatalog.ConfigureForDiagnostics(null, true);
        int count = ImportedTreeLeafMipCatalog.Apply(variants, resource, fingerprint);
        reason = ImportedTreeLeafMipCatalog.FallbackReason; warnings = ImportedTreeLeafMipCatalog.WarningCount;
        return count;
    }

    private static PreparedTreeLeafMips Clone(PreparedTreeLeafMips source) => new()
    {
        SchemaVersion = source.SchemaVersion, ToolVersion = source.ToolVersion, CatalogFingerprint = source.CatalogFingerprint,
        AtlasNames = (string[])source.AtlasNames.Clone(), SourcePaths = (string[])source.SourcePaths.Clone(), Cutoffs = (float[])source.Cutoffs.Clone(),
        Sizes = (int[])source.Sizes.Clone(), Textures = new Godot.Collections.Array<ImageTexture>(source.Textures)
    };

    private static IEnumerable<ShaderMaterial> LeafMaterials(IReadOnlyList<ImportedTreeVariant> variants)
    {
        var seen = new HashSet<ShaderMaterial>();
        foreach (ImportedTreeVariant v in variants)
            foreach (Mesh mesh in v.Lods)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    if (mesh.SurfaceGetMaterial(s) is ShaderMaterial m && seen.Add(m) && m.GetShaderParameter("tree_foliage_mode").AsInt32() == 1 && m.GetShaderParameter("use_alpha_cutout").AsBool())
                        yield return m;
    }
}
