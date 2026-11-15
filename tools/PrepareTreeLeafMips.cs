using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7c: builds the distance-stable leaf mips of the three leaf atlases (TreeLeafMipBuilder), compresses them level by level into the import's format and gates the result (the scale bisection is
/// closed on the DXT5 round trip, TR.md "TR-7c-PV DXT5 note"). Preview mode (shippedPath empty) writes both the open-loop and the closed-loop ImageTextures, manifest.json and leaf_mips_table.md under res://.godot/
/// (it refuses any other output folder). Shipped mode builds the closed loop only and, when every gate passes, stages and promotes resources/trees/prepared/lolipop_leaf_mips.res and its manifest.</summary>
public partial class PrepareTreeLeafMips : RefCounted
{
    public const string ToolVersion = "tr7c-2";
    private const string ReferenceOutput = "res://.godot/tr7c_lead/leaf_mips_reference_output.txt";
    private const string ColourReferenceOutput = "res://.godot/tr7c_lead2/colour_rule_reference_output.txt";

    internal sealed class Atlas
    {
        public string Name = "", Path = "";
        public float Cutoff; public bool HasCutoff;
        public Texture2D Texture = null!;
        public readonly SortedSet<int> Variants = new();
        public readonly Dictionary<int, double[]> Usage = new();
    }

    public int Run(string outDir, string reportPath, string shippedPath, string manifestPath)
    {
        var failures = new List<string>();
        var openMisses = new List<string>();
        bool shipped = !string.IsNullOrEmpty(shippedPath);
        try
        {
            if (!outDir.StartsWith("res://.godot/")) throw new ArgumentException("output folder must be under res://.godot/: " + outDir);
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(outDir));
            ImportedTreeLeafMipCatalog.ConfigureForDiagnostics(null, false); // the catalog must hand out the import's own textures: they are what is rebuilt
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            if (shipped && ImportedTreeCatalog.LoadedFrom != "prepared") throw new InvalidOperationException("the shipped resource is built against the prepared catalog; it loaded from " + ImportedTreeCatalog.LoadedFrom + " (" + ImportedTreeCatalog.FallbackReason + ")");
            string catalogFingerprint = shipped ? ResourceLoader.Load<PreparedImportedTreeCatalog>(ImportedTreeCatalog.PreparedResourcePath).SourceFingerprint : "";
            var product = new PreparedTreeLeafMips { ToolVersion = ToolVersion, CatalogFingerprint = catalogFingerprint };
            var tableRows = new List<object>(); var colourRows = new List<object>();
            var atlases = Group(variants, failures);
            var manifest = new List<object>();
            var md = new StringBuilder();
            md.AppendLine("# TR-7c-PV leaf mip table (stable mips; per-variant passing area against level 0, usage weighted)");
            md.AppendLine();
            var reference = ParseReference();
            var colourReference = ParseColourReference();
            foreach (Atlas atlas in atlases)
            {
                var clock = Stopwatch.StartNew();
                Image import = atlas.Texture.GetImage();
                if (import.GetFormat() != Image.Format.Dxt5) { failures.Add($"{atlas.Name}: the import is {import.GetFormat()}, not DXT5"); continue; }
                int size = import.GetWidth();
                int levels = 0; while ((1 << levels) < size) levels++;
                if (import.GetHeight() != size || import.GetMipmapCount() != levels) { failures.Add($"{atlas.Name}: unexpected import shape {size}x{import.GetHeight()}, {import.GetMipmapCount()} mips"); continue; }
                byte[] importData = import.GetData();
                int level0Bytes = (int)import.GetMipmapOffset(1);
                Image decoded = (Image)import.Duplicate();
                decoded.Decompress();
                decoded.ClearMipmaps();
                decoded.Convert(Image.Format.Rgba8);
                byte[] rgba = decoded.GetData();
                if (rgba.Length != size * size * 4) { failures.Add($"{atlas.Name}: decoded level 0 has {rgba.Length} bytes"); continue; }
                double cutoff = atlas.Cutoff;
                double[] species = new double[TreeLeafMipBuilder.Grid * TreeLeafMipBuilder.Grid];
                foreach (var pair in atlas.Usage) { double sum = pair.Value.Sum(); for (int i = 0; i < species.Length; i++) species[i] += pair.Value[i] / sum; }
                foreach (bool closed in shipped ? new[] { true } : new[] { false, true })
                {
                var sink = closed ? failures : openMisses;
                var built = TreeLeafMipBuilder.Build(rgba, size, cutoff, species, closed ? Codec : null, 256, closed ? ColourCodec : null, closed ? LevelCodec : null);
                var again = TreeLeafMipBuilder.Build(rgba, size, cutoff, species, closed ? Codec : null, 256, closed ? ColourCodec : null, closed ? LevelCodec : null);
                string buildHash = HashBuilt(built);
                if (buildHash != HashBuilt(again)) sink.Add($"{atlas.Name}: a second build gives another hash");
                // compress level by level and concatenate behind the import's level 0
                var chain = new List<byte>(importData.Length);
                chain.AddRange(new ArraySegment<byte>(importData, 0, level0Bytes));
                var levelBytes = new byte[levels + 1][];
                for (int l = 1; l <= levels; l++)
                {
                    levelBytes[l] = CompressLevel(built.Rgb[l], built.Alpha[l], size >> l);
                    int expected = (int)((l == levels ? importData.Length : import.GetMipmapOffset(l + 1)) - import.GetMipmapOffset(l));
                    if (levelBytes[l].Length != expected) sink.Add($"{atlas.Name}: level {l} has {levelBytes[l].Length} bytes, the import {expected}");
                    chain.AddRange(levelBytes[l]);
                }
                byte[] chainData = chain.ToArray();
                if (chainData.Length != importData.Length) sink.Add($"{atlas.Name}: chain length {chainData.Length} differs from the import's {importData.Length}");
                bool level0Same = chainData.AsSpan(0, level0Bytes).SequenceEqual(importData.AsSpan(0, level0Bytes));
                if (!level0Same) sink.Add($"{atlas.Name}: level 0 differs from the import");
                Image output = Image.CreateFromData(size, size, true, Image.Format.Dxt5, chainData);
                if (output == null || output.GetMipmapCount() != levels || output.GetData().Length != importData.Length) { sink.Add($"{atlas.Name}: the chain does not load with its built levels"); break; }
                for (int l = 1; l <= levels; l++)
                {
                    var slice = output.GetData().AsSpan((int)output.GetMipmapOffset(l), levelBytes[l].Length);
                    if (!slice.SequenceEqual(levelBytes[l])) { sink.Add($"{atlas.Name}: level {l} was not kept by the loaded chain"); break; }
                }
                // decompressed alpha of every built level
                var decodedAlpha = new byte[levels + 1][]; var decodedRgb = new byte[levels + 1][];
                for (int l = 1; l <= levels; l++) DecodeLevel(levelBytes[l], size >> l, out decodedAlpha[l], out decodedRgb[l]);
                var texture = ImageTexture.CreateFromImage(output);
                string file = $"atlas_{atlas.Name}{(closed ? "_cl" : "")}.res";
                if (shipped)
                {
                    product.AtlasNames = product.AtlasNames.Append(atlas.Name).ToArray();
                    product.SourcePaths = product.SourcePaths.Append(atlas.Path).ToArray();
                    product.Cutoffs = product.Cutoffs.Append((float)cutoff).ToArray();
                    product.Sizes = product.Sizes.Append(size).ToArray();
                    product.Textures.Add(texture);
                }
                else if (ResourceSaver.Save(texture, $"{outDir}/{file}") != Error.Ok) sink.Add($"{atlas.Name}: could not save {file}");
                // the table
                md.AppendLine($"## {atlas.Name} ({(closed ? "closed loop: scales solved on the DXT5 round trip" : "open loop: the rule as specified")}): {size} px, cutoff {cutoff:0.###}, islands {built.Islands}, variants {string.Join(",", atlas.Variants)}");
                md.AppendLine();
                md.AppendLine("level | texels | built: mean (min..max) | halfway to next | decompressed DXT5: mean (min..max) | halfway | scale per island");
                md.AppendLine("--- | --- | --- | --- | --- | --- | ---");
                var baseArea = atlas.Variants.ToDictionary(v => v, v => TreeLeafMipBuilder.BaselineArea(rgba, size, cutoff, atlas.Usage[v]));
                for (int l = 1; l <= levels; l++)
                {
                    int s = size >> l;
                    if (s > TreeLeafMipBuilder.Grid || s < 2) continue;
                    var built1 = new List<double>(); var half1 = new List<double>(); var dec1 = new List<double>(); var dhalf1 = new List<double>();
                    foreach (int v in atlas.Variants)
                    {
                        double b = baseArea[v];
                        built1.Add(TreeLeafMipBuilder.AreaOf(built.Alpha[l], s, cutoff, atlas.Usage[v]) / b);
                        dec1.Add(TreeLeafMipBuilder.AreaOf(decodedAlpha[l], s, cutoff, atlas.Usage[v]) / b);
                        if (l < levels)
                        {
                            half1.Add(TreeLeafMipBuilder.AreaHalfway(built.Alpha[l], s, built.Alpha[l + 1], s / 2, cutoff, atlas.Usage[v]) / b);
                            dhalf1.Add(TreeLeafMipBuilder.AreaHalfway(decodedAlpha[l], s, decodedAlpha[l + 1], s / 2, cutoff, atlas.Usage[v]) / b);
                        }
                    }
                    tableRows.Add(new { atlas = atlas.Name, level = l, texels = s, built_mean = Math.Round(built1.Average(), 4), built_min = Math.Round(built1.Min(), 4), built_max = Math.Round(built1.Max(), 4), dxt5_mean = Math.Round(dec1.Average(), 4), dxt5_min = Math.Round(dec1.Min(), 4), dxt5_max = Math.Round(dec1.Max(), 4) });
                    md.AppendLine($"L{l} | {s} | {Fmt(built1)} | {Fmt(half1)} | {Fmt(dec1)} | {Fmt(dhalf1)} | {string.Join(" ", built.Scales[l].Select(x => x.ToString("0.00")))}");
                    double mean = dec1.Average();
                    foreach (string miss in GateLevel(atlas.Name, s, dec1)) sink.Add(miss);
                    if (s >= 8 && reference.TryGetValue((atlas.Name, s), out double refMean) && Math.Abs(mean - refMean) > MeanTolerance(atlas.Name, s))
                        sink.Add($"{atlas.Name} L{l} ({s}x{s}): table mean {mean:0.000} differs from the reference table's {refMean:0.000} by more than 2 points");
                }
                if (decodedAlpha[levels][0] / 255.0 >= cutoff || built.Alpha[levels][0] / 255.0 >= cutoff) sink.Add($"{atlas.Name}: the 1x1 level passes the cutoff");
                md.AppendLine();
                // colour table: the passing area's colour against the close-up colour, on the decoded chain (and the built, undecoded colours for the reference comparison), at the level and halfway to the next
                double[] closeUp = TreeLeafMipBuilder.BaselineColour(rgba, size, cutoff, species);
                if (closed) md.AppendLine($"Close-up colour (linear rgb, usage weighted): decoded import level 0 (the gates' baseline) {FmtRgb(closeUp)}; source PNG {FmtRgb(SourceBaseline(atlas.Path, size, cutoff, species))}");
                var plain = closed ? TreeLeafMipBuilder.Build(rgba, size, cutoff, species) : built; // the rule's own colours (no codec, no snap): what the colour reference describes
                md.AppendLine($"### {atlas.Name} colour of the passing area against the close-up colour (decoded DXT5; luminance, then each channel's difference over the close-up luminance; species usage)");
                md.AppendLine();
                md.AppendLine("level | texels | at the level: lum (dr dg db) | halfway to next: lum (dr dg db) | built, undecoded: lum");
                md.AppendLine("--- | --- | --- | --- | ---");
                for (int l = 1; l <= levels; l++)
                {
                    int s = size >> l;
                    if (s > TreeLeafMipBuilder.Grid || s < 2) continue;
                    double[]? at = TreeLeafMipBuilder.PassColour(decodedAlpha[l], decodedRgb[l], s, cutoff, species);
                    double[]? half = l < levels ? TreeLeafMipBuilder.PassColourHalfway(decodedAlpha[l], decodedRgb[l], s, decodedAlpha[l + 1], decodedRgb[l + 1], s / 2, cutoff, species) : null;
                    double[]? undecoded = TreeLeafMipBuilder.PassColour(plain.Alpha[l], plain.Rgb[l], s, cutoff, species);
                    md.AppendLine($"L{l} | {s} | {FmtColour(at, closeUp)} | {FmtColour(half, closeUp)} | {(undecoded == null ? "-" : (TreeLeafMipBuilder.Luminance(undecoded) / TreeLeafMipBuilder.Luminance(closeUp)).ToString("0.000"))}");
                    if (at != null)
                    {
                        colourRows.Add(new { atlas = atlas.Name, mode = closed ? "closed" : "open", level = l, texels = s, luminance = Math.Round(TreeLeafMipBuilder.Luminance(at) / TreeLeafMipBuilder.Luminance(closeUp), 4), dr = Math.Round(ChannelError(at, closeUp, 0), 4), dg = Math.Round(ChannelError(at, closeUp, 1), 4), db = Math.Round(ChannelError(at, closeUp, 2), 4) });
                        foreach (string miss in GateColour(atlas.Name, s, at, closeUp)) sink.Add(miss);
                    }
                    if (closed && undecoded != null && colourReference.TryGetValue((atlas.Name, s), out double refLum))
                    {
                        double mine = TreeLeafMipBuilder.Luminance(undecoded) / TreeLeafMipBuilder.Luminance(closeUp);
                        if (Math.Abs(mine - refLum) > 0.02) sink.Add($"{atlas.Name} L{l} ({s}x{s}): undecoded colour luminance {mine:0.000} differs from the colour reference's hyb {refLum:0.000} by more than 2 points");
                    }
                }
                md.AppendLine();
                manifest.Add(new
                {
                    name = atlas.Name, mode = closed ? "closed" : "open", source = atlas.Path, cutoff, size, file, levels, islands = built.Islands, variants = atlas.Variants.ToArray(),
                    chain_sha256 = Sha(chainData), build_sha256 = buildHash, level0_equals_import = level0Same, chain_bytes = chainData.Length,
                    import_mips = import.GetMipmapCount(), seconds = Math.Round(clock.Elapsed.TotalSeconds, 1)
                });
                GD.Print($"PREPARE_TREE_LEAF_MIPS {atlas.Name} {(closed ? "closed" : "open")}: {size} px, islands {built.Islands}, chain {chainData.Length} bytes, {clock.Elapsed.TotalSeconds:0.0} s");
                }
            }
            if (atlases.Count != 3) failures.Add($"expected 3 leaf atlases, found {atlases.Count}");
            File.WriteAllText(ProjectSettings.GlobalizePath($"{outDir}/leaf_mips_table.md"), md.ToString());
            File.WriteAllText(ProjectSettings.GlobalizePath($"{outDir}/manifest.json"),
                JsonSerializer.Serialize(new { tool_version = ToolVersion, atlases = manifest, failures, open_loop_gate_misses = openMisses }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(ProjectSettings.GlobalizePath(reportPath), md.ToString());
            if (failures.Count == 0 && shipped) Promote(product, shippedPath, manifestPath, manifest, tableRows, colourRows, catalogFingerprint);
            if (failures.Count > 0)
            {
                foreach (string f in failures) GD.PushError("PREPARE_TREE_LEAF_MIPS gate: " + f);
                GD.PushError($"PREPARE_TREE_LEAF_MIPS=FAIL {failures.Count} gate failures");
                return 1;
            }
            foreach (string m in openMisses) GD.Print("PREPARE_TREE_LEAF_MIPS open-loop gate miss (information): " + m);
            GD.Print("PREPARE_TREE_LEAF_MIPS=PASS (closed loop) atlases=" + atlases.Count + (shipped ? " shipped=" + shippedPath : " out=" + outDir));
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_TREE_LEAF_MIPS=FAIL " + exception);
            return 1;
        }
    }

    /// <summary>Species-mean tolerance of one level. TR.md "TR-7c-PV DXT5 note (2026-11-15)": maple's 8 x 8 level is bounded at the measured DXT5 floor (3 %).</summary>
    internal static double MeanTolerance(string atlas, int texels) => atlas == "maple" && texels == 8 ? 0.03 : 0.02;

    /// <summary>The gates of one level on the decoded passing-area ratios of all variants of an atlas (against level 0): species mean (2 %, maple 8 x 8 3 %) and variants (5 %) from 8 x 8 texels up,
    /// variants at 4 x 4 within 10 % (pine 20 %, the measured DXT5 floor). Returns the misses.</summary>
    internal static List<string> GateLevel(string atlas, int texels, List<double> ratios)
    {
        var misses = new List<string>();
        double mean = ratios.Average(), tolerance = MeanTolerance(atlas, texels), variantTolerance4 = atlas == "pine" ? 0.20 : 0.10;
        string range = $"{ratios.Min():0.000}..{ratios.Max():0.000}";
        if (texels >= 8)
        {
            if (Math.Abs(mean - 1) > tolerance) misses.Add($"{atlas} ({texels}x{texels}): species mean {mean:0.000} is off by more than {tolerance * 100:0} % after DXT5");
            if (ratios.Any(r => Math.Abs(r - 1) > 0.05)) misses.Add($"{atlas} ({texels}x{texels}): a variant is off by more than 5 % after DXT5 ({range})");
        }
        else if (texels == 4 && ratios.Any(r => Math.Abs(r - 1) > variantTolerance4)) misses.Add($"{atlas} (4x4): a variant is off by more than {variantTolerance4 * 100:0} % after DXT5 ({range})");
        return misses;
    }

    /// <summary>Channel difference of a passing-area colour from the close-up colour over the close-up luminance (the match test's definition).</summary>
    internal static double ChannelError(double[] pass, double[] closeUp, int channel) => Math.Abs(pass[channel] - closeUp[channel]) / Math.Max(TreeLeafMipBuilder.Luminance(closeUp), 1e-6);

    /// <summary>Colour gates of one level (species usage, decoded): 16 texels and smaller luminance within 4 % and each channel within 8 % of the luminance (a flat colour sits on the 5:6:5 grid); 32 to 512 texels
    /// luminance within 5 % and each channel within 6 % (fir is 2.7 to 3.4 % dark at 64 and 32 texels, limit F3, kept as a bound).</summary>
    internal static List<string> GateColour(string atlas, int texels, double[] pass, double[] closeUp)
    {
        var misses = new List<string>();
        bool small = texels <= 16;
        double lumTolerance = small ? 0.04 : 0.05, channelTolerance = small ? 0.08 : 0.06;
        double lum = TreeLeafMipBuilder.Luminance(pass) / TreeLeafMipBuilder.Luminance(closeUp);
        if (Math.Abs(lum - 1) > lumTolerance) misses.Add($"{atlas} ({texels}x{texels}): colour luminance {lum:0.000} of the close-up is off by more than {lumTolerance * 100:0} % after DXT5");
        for (int c = 0; c < 3; c++)
            if (ChannelError(pass, closeUp, c) > channelTolerance) misses.Add($"{atlas} ({texels}x{texels}): colour channel {"rgb"[c]} differs by {ChannelError(pass, closeUp, c):0.000} of the close-up luminance (limit {channelTolerance:0.00}) after DXT5");
        return misses;
    }

    private static string FmtRgb(double[]? c) => c == null ? "n/a" : $"({c[0]:0.0000} {c[1]:0.0000} {c[2]:0.0000}) lum {TreeLeafMipBuilder.Luminance(c):0.0000}";

    /// <summary>The same baseline from the uncompressed source image next to the import, when there is one (information only).</summary>
    private static double[]? SourceBaseline(string importPath, int size, double cutoff, double[] species)
    {
        try
        {
            string png = System.IO.Path.ChangeExtension(ProjectSettings.GlobalizePath(importPath), ".png");
            if (!File.Exists(png)) return null;
            Image image = Image.LoadFromFile(png);
            if (image == null || image.GetWidth() != size) return null;
            image.Convert(Image.Format.Rgba8);
            return TreeLeafMipBuilder.BaselineColour(image.GetData(), size, cutoff, species);
        }
        catch (Exception) { return null; }
    }

    private static string FmtColour(double[]? pass, double[] closeUp)
        => pass == null ? "-" : $"{TreeLeafMipBuilder.Luminance(pass) / TreeLeafMipBuilder.Luminance(closeUp):0.000} ({SignedChannel(pass, closeUp, 0):+0.000;-0.000} {SignedChannel(pass, closeUp, 1):+0.000;-0.000} {SignedChannel(pass, closeUp, 2):+0.000;-0.000})";

    private static double SignedChannel(double[] pass, double[] closeUp, int channel) => (pass[channel] - closeUp[channel]) / Math.Max(TreeLeafMipBuilder.Luminance(closeUp), 1e-6);

    /// <summary>Stages the resource and the manifest next to their targets and promotes both (move over the old file) only after the resource saved and re-loaded with the right shape.</summary>
    private static void Promote(PreparedTreeLeafMips product, string shippedPath, string manifestPath, List<object> atlases, List<object> table, List<object> colourTable, string catalogFingerprint)
    {
        string target = ProjectSettings.GlobalizePath(shippedPath), staged = System.IO.Path.ChangeExtension(target, ".staged.res");
        if (ResourceSaver.Save(product, ProjectSettings.LocalizePath(staged)) != Error.Ok) throw new IOException("could not stage " + staged);
        var check = ResourceLoader.Load<PreparedTreeLeafMips>(ProjectSettings.LocalizePath(staged), null, ResourceLoader.CacheMode.Ignore);
        if (check == null || check.Textures.Count != 3 || check.CatalogFingerprint != catalogFingerprint) throw new IOException("the staged resource does not load back");
        string stagedManifest = manifestPath.Length > 0 ? ProjectSettings.GlobalizePath(manifestPath) + ".staged" : "";
        string hash = Sha(File.ReadAllBytes(staged));
        var manifest = new
        {
            schema_version = PreparedTreeLeafMips.CurrentSchemaVersion, tool_version = ToolVersion, catalog_fingerprint = catalogFingerprint,
            engine_identity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity, resource_path = "resources/trees/prepared/lolipop_leaf_mips.res",
            artifact_sha256 = hash, artifact_bytes = new FileInfo(staged).Length,
            note = "levels 1 and up are the stable mips (alpha: covered fraction scaled per leaf cluster on the DXT5 round trip; colour: linear-light mean of the covered texels, usage weighted, smoothed down to 32 texels, the leaf cluster's mean colour at 16 to 4 texels and the atlas mean at 2 and 1, flat colours snapped on the DXT5 round trip); level 0 is the import's bytes",
            atlases, table, colour_table = colourTable
        };
        File.WriteAllText(stagedManifest, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(staged, target, true);
        File.Move(stagedManifest, ProjectSettings.GlobalizePath(manifestPath), true);
        GD.Print($"PREPARE_TREE_LEAF_MIPS promoted {shippedPath} ({new FileInfo(target).Length} bytes, sha256 {hash})");
    }

    /// <summary>Level 0 of a DXT5 import decoded to RGBA8 (the builder's input).</summary>
    internal static byte[] DecodeLevel0(Image import)
    {
        Image decoded = (Image)import.Duplicate();
        decoded.Decompress();
        decoded.ClearMipmaps();
        decoded.Convert(Image.Format.Rgba8);
        return decoded.GetData();
    }

    /// <summary>The usage map of a species: every variant's map, each normalised to 1, summed.</summary>
    internal static double[] SpeciesUsage(Atlas atlas)
    {
        double[] species = new double[TreeLeafMipBuilder.Grid * TreeLeafMipBuilder.Grid];
        foreach (var pair in atlas.Usage) { double sum = pair.Value.Sum(); for (int i = 0; i < species.Length; i++) species[i] += pair.Value[i] / sum; }
        return species;
    }

    /// <summary>Alpha bytes of every level (index 1 and up) of a DXT5 chain, as the GPU decodes them.</summary>
    internal static byte[][] DecodeChain(Image chain, int size) => DecodeChain(chain, size, out _);

    /// <summary>Alpha bytes and sRGB colour bytes (s x s x 3) of every level (index 1 and up) of a DXT5 chain, as the GPU decodes them.</summary>
    internal static byte[][] DecodeChain(Image chain, int size, out byte[][] rgb)
    {
        int levels = chain.GetMipmapCount();
        byte[] data = chain.GetData();
        var result = new byte[levels + 1][]; rgb = new byte[levels + 1][];
        for (int l = 1; l <= levels; l++)
        {
            int offset = (int)chain.GetMipmapOffset(l), end = l == levels ? data.Length : (int)chain.GetMipmapOffset(l + 1);
            DecodeLevel(data.AsSpan(offset, end - offset).ToArray(), size >> l, out result[l], out rgb[l]);
        }
        return result;
    }

    /// <summary>Per level (index 1 and up, 512 texels and smaller; others null) the colour of the passing area against the close-up colour: (luminance ratio, channel errors r, g, b), species usage, from decoded levels.</summary>
    internal static double[]?[] ColourLevels(Atlas atlas, byte[] rgba, int size, byte[][] alphaLevels, byte[][] rgbLevels, out double[] closeUp)
    {
        double[] species = SpeciesUsage(atlas);
        closeUp = TreeLeafMipBuilder.BaselineColour(rgba, size, atlas.Cutoff, species);
        var result = new double[]?[alphaLevels.Length];
        for (int l = 1; l < alphaLevels.Length; l++)
        {
            int s = size >> l;
            if (s > TreeLeafMipBuilder.Grid || s < 2) continue;
            double[]? at = TreeLeafMipBuilder.PassColour(alphaLevels[l], rgbLevels[l], s, atlas.Cutoff, species);
            if (at == null) continue;
            result[l] = new[] { TreeLeafMipBuilder.Luminance(at) / TreeLeafMipBuilder.Luminance(closeUp), ChannelError(at, closeUp, 0), ChannelError(at, closeUp, 1), ChannelError(at, closeUp, 2) };
        }
        return result;
    }

    /// <summary>The colour gates of a set of colour levels (from ColourLevels).</summary>
    internal static List<string> GateColourLevels(string atlas, int size, double[]?[] levels)
    {
        var misses = new List<string>();
        for (int l = 1; l < levels.Length; l++)
        {
            if (levels[l] == null) continue;
            int s = size >> l; bool small = s <= 16;
            double lumTolerance = small ? 0.04 : 0.05, channelTolerance = small ? 0.08 : 0.06;
            if (Math.Abs(levels[l]![0] - 1) > lumTolerance) misses.Add($"{atlas} ({s}x{s}): colour luminance {levels[l]![0]:0.000} of the close-up");
            for (int c = 0; c < 3; c++) if (levels[l]![1 + c] > channelTolerance) misses.Add($"{atlas} ({s}x{s}): colour channel {"rgb"[c]} differs by {levels[l]![1 + c]:0.000}");
        }
        return misses;
    }

    /// <summary>Per level (index 1 and up, levels from 2 x 2 up to the 512 grid; others null) the passing area of every variant of the atlas against level 0 (usage weighted), from decoded alpha levels.</summary>
    internal static Dictionary<int, double>[] LevelRatios(Atlas atlas, byte[] rgba, int size, byte[][] alphaLevels)
    {
        var baseArea = atlas.Variants.ToDictionary(v => v, v => TreeLeafMipBuilder.BaselineArea(rgba, size, atlas.Cutoff, atlas.Usage[v]));
        var result = new Dictionary<int, double>[alphaLevels.Length];
        for (int l = 1; l < alphaLevels.Length; l++)
        {
            int s = size >> l;
            if (s > TreeLeafMipBuilder.Grid || s < 2) continue;
            result[l] = atlas.Variants.ToDictionary(v => v, v => TreeLeafMipBuilder.AreaOf(alphaLevels[l], s, atlas.Cutoff, atlas.Usage[v]) / baseArea[v]);
        }
        return result;
    }

    internal static string BuildAndDecode(Atlas atlas, byte[] rgba, int size, out byte[][] decodedAlpha, out byte[][] decodedRgb)
    {
        var built = TreeLeafMipBuilder.Build(rgba, size, atlas.Cutoff, SpeciesUsage(atlas), Codec, 256, ColourCodec, LevelCodec);
        decodedAlpha = new byte[built.Levels + 1][]; decodedRgb = new byte[built.Levels + 1][];
        for (int l = 1; l <= built.Levels; l++) DecodeLevel(CompressLevel(built.Rgb[l], built.Alpha[l], size >> l), size >> l, out decodedAlpha[l], out decodedRgb[l]);
        return HashBuilt(built);
    }

    private static byte[] Codec(byte[] alpha, int s) => DecodeAlpha(CompressLevel(new byte[s * s * 3], alpha, s), s);

    /// <summary>The level codec of the builder's colour loop: a whole level through compress and decode (alpha and colour).</summary>
    private static (byte[] Alpha, byte[] Rgb) LevelCodec(byte[] rgb, byte[] alpha, int s)
    {
        DecodeLevel(CompressLevel(rgb, alpha, s), s, out byte[] a, out byte[] c);
        return (a, c);
    }

    /// <summary>The colour codec of the builder: a 4 x 4 block of sRGB bytes through the same compress call the chain uses (alpha opaque), decoded.</summary>
    private static byte[] ColourCodec(byte[] block)
    {
        var alpha = new byte[16]; Array.Fill(alpha, (byte)255);
        DecodeLevel(CompressLevel(block, alpha, 4), 4, out _, out byte[] rgb);
        return rgb;
    }

    private static string Fmt(List<double> v) => v.Count == 0 ? "-" : $"{v.Average():0.000} ({v.Min():0.000}..{v.Max():0.000})";

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string HashBuilt(TreeLeafMipBuilder.Built b)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int l = 1; l <= b.Levels; l++) { sha.AppendData(b.Alpha[l]); sha.AppendData(b.Rgb[l]); }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>DXT5 bytes of one level. A level under 4 x 4 texels is one block: the texels are tiled into a 4 x 4 image and compressed, so the texel (x, y) of the level is the block's texel (x, y).</summary>
    public static byte[] CompressLevel(byte[] rgb, byte[] alpha, int s)
    {
        int p = Math.Max(s, 4);
        var data = new byte[p * p * 4];
        for (int y = 0; y < p; y++)
            for (int x = 0; x < p; x++)
            {
                int src = (y % s) * s + x % s, dst = (y * p + x) * 4;
                data[dst] = rgb[src * 3]; data[dst + 1] = rgb[src * 3 + 1]; data[dst + 2] = rgb[src * 3 + 2]; data[dst + 3] = alpha[src];
            }
        Image image = Image.CreateFromData(p, p, false, Image.Format.Rgba8, data);
        image.CompressFromChannels(Image.CompressMode.S3Tc, Image.UsedChannels.Rgba);
        if (image.GetFormat() != Image.Format.Dxt5) throw new InvalidOperationException("compress produced " + image.GetFormat());
        return image.GetData();
    }

    /// <summary>Alpha bytes (s x s) of one DXT5 level as the GPU would decode it.</summary>
    public static byte[] DecodeAlpha(byte[] dxt5, int s) { DecodeLevel(dxt5, s, out byte[] alpha, out _); return alpha; }

    /// <summary>Alpha bytes (s x s) and sRGB colour bytes (s x s x 3) of one DXT5 level as the GPU would decode them.</summary>
    public static void DecodeLevel(byte[] dxt5, int s, out byte[] alpha, out byte[] rgb)
    {
        int p = Math.Max(s, 4);
        Image image = Image.CreateFromData(p, p, false, Image.Format.Dxt5, dxt5);
        image.Decompress();
        image.Convert(Image.Format.Rgba8);
        byte[] rgba = image.GetData();
        alpha = new byte[s * s]; rgb = new byte[s * s * 3];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                int src = (y * p + x) * 4, dst = y * s + x;
                alpha[dst] = rgba[src + 3]; rgb[dst * 3] = rgba[src]; rgb[dst * 3 + 1] = rgba[src + 1]; rgb[dst * 3 + 2] = rgba[src + 2];
            }
    }

    // ---- catalog grouping and usage -------------------------------------------------------------------------------------------------------------------

    internal static List<Atlas> Group(IReadOnlyList<ImportedTreeVariant> variants, List<string> failures)
    {
        var atlases = new Dictionary<string, Atlas>();
        foreach (ImportedTreeVariant v in variants)
            for (int lod = 0; lod < v.Lods.Length; lod++)
                for (int s = 0; s < v.Lods[lod].GetSurfaceCount(); s++)
                {
                    if (!IsLeafMaterial(v.Lods[lod].SurfaceGetMaterial(s), out ShaderMaterial? m, out Texture2D? tex)) continue;
                    string path = tex!.ResourcePath;
                    if (!atlases.TryGetValue(path, out Atlas? a))
                    {
                        string[] parts = path.Split('/');
                        int at = Array.IndexOf(parts, "textures");
                        atlases[path] = a = new Atlas { Path = path, Name = at > 0 ? parts[at - 1] : System.IO.Path.GetFileNameWithoutExtension(path), Texture = tex };
                    }
                    float cutoff = m!.GetShaderParameter("alpha_cutoff").AsSingle();
                    if (!a.HasCutoff) { a.Cutoff = cutoff; a.HasCutoff = true; }
                    else if (a.Cutoff != cutoff) failures.Add($"{a.Name}: two cutoffs ({a.Cutoff} and {cutoff}) on one atlas");
                    a.Variants.Add(v.Index);
                }
        foreach (Atlas a in atlases.Values)
            foreach (int index in a.Variants)
            {
                ImportedTreeVariant v = variants[index];
                var map = new double[TreeLeafMipBuilder.Grid * TreeLeafMipBuilder.Grid];
                foreach (int lod in new[] { 0, 2 })
                {
                    var lodMap = new double[map.Length];
                    for (int s = 0; s < v.Lods[lod].GetSurfaceCount(); s++)
                    {
                        if (!IsLeafMaterial(v.Lods[lod].SurfaceGetMaterial(s), out _, out Texture2D? tex) || tex!.ResourcePath != a.Path) continue;
                        Splat((ArrayMesh)v.Lods[lod], s, v.HeightMeters, lodMap);
                    }
                    double sum = lodMap.Sum();
                    if (sum <= 0) { failures.Add($"{a.Name}: variant {index} LOD{lod} has no leaf area"); continue; }
                    for (int i = 0; i < map.Length; i++) map[i] += lodMap[i] / sum;
                }
                a.Usage[index] = map;
            }
        return atlases.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsLeafMaterial(Material? material, out ShaderMaterial? shader, out Texture2D? texture)
    {
        shader = material as ShaderMaterial; texture = null;
        if (shader == null || shader.GetShaderParameter("tree_foliage_mode").AsInt32() != 1 || !shader.GetShaderParameter("use_alpha_cutout").AsBool()) return false;
        texture = shader.GetShaderParameter("albedo_texture").AsGodotObject() as Texture2D;
        return texture != null;
    }

    /// <summary>Adds the world area of every triangle of a surface to the 512 x 512 UV cells it covers (equal share per cell centre inside; a triangle covering no centre goes to its centroid's cell).</summary>
    private static void Splat(ArrayMesh mesh, int surface, float height, double[] map)
    {
        const int G = TreeLeafMipBuilder.Grid;
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
        Vector3[] pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        int[] idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            double area = (pos[idx[t + 1]] - pos[idx[t]]).Cross(pos[idx[t + 2]] - pos[idx[t]]).Length() * 0.5 * height * height;
            if (area < 1e-9) continue;
            double[] u = { uv[idx[t]].X * G, uv[idx[t + 1]].X * G, uv[idx[t + 2]].X * G };
            double[] w = { uv[idx[t]].Y * G, uv[idx[t + 1]].Y * G, uv[idx[t + 2]].Y * G };
            double d = (u[1] - u[0]) * (w[2] - w[0]) - (u[2] - u[0]) * (w[1] - w[0]);
            if (Math.Abs(d) < 1e-9) continue;
            int x0 = (int)Math.Max(0, Math.Floor(u.Min())), x1 = (int)Math.Min(G - 1, Math.Ceiling(u.Max()));
            int y0 = (int)Math.Max(0, Math.Floor(w.Min())), y1 = (int)Math.Min(G - 1, Math.Ceiling(w.Max()));
            if (x1 < x0 || y1 < y0) continue;
            var inside = new List<int>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + .5, py = y + .5;
                    double w0 = ((u[1] - px) * (w[2] - py) - (u[2] - px) * (w[1] - py)) / d;
                    double w1 = ((u[2] - px) * (w[0] - py) - (u[0] - px) * (w[2] - py)) / d;
                    double w2 = 1 - w0 - w1;
                    if (w0 >= 0 && w1 >= 0 && w2 >= 0) inside.Add(y * G + x);
                }
            if (inside.Count == 0)
            {
                int cx = Math.Clamp((int)((u[0] + u[1] + u[2]) / 3), 0, G - 1), cy = Math.Clamp((int)((w[0] + w[1] + w[2]) / 3), 0, G - 1);
                map[cy * G + cx] += area;
            }
            else foreach (int c in inside) map[c] += area / inside.Count;
        }
    }

    /// <summary>Luminance of the passing area against the close-up colour, rule hyb, per level of the lead's colour reference, keyed by (atlas, texels).</summary>
    private static Dictionary<(string, int), double> ParseColourReference()
    {
        var result = new Dictionary<(string, int), double>();
        string path = ProjectSettings.GlobalizePath(ColourReferenceOutput);
        if (!File.Exists(path)) return result;
        string atlas = "";
        foreach (string line in File.ReadLines(path))
        {
            Match head = Regex.Match(line, @"^== (\w+):");
            if (head.Success) { atlas = head.Groups[1].Value; continue; }
            Match row = Regex.Match(line, @"^\s+L\d+\s+\(\s*(\d+)\).*\| hyb: ([0-9.]+) \(");
            if (row.Success && atlas.Length > 0) result[(atlas, int.Parse(row.Groups[1].Value))] = double.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>Species mean per level of the lead's reference table, keyed by (atlas, texels).</summary>
    private static Dictionary<(string, int), double> ParseReference()
    {
        var result = new Dictionary<(string, int), double>();
        string path = ProjectSettings.GlobalizePath(ReferenceOutput);
        if (!File.Exists(path)) return result;
        string atlas = "";
        foreach (string line in File.ReadLines(path))
        {
            Match head = Regex.Match(line, @"^== (\w+):");
            if (head.Success) { atlas = head.Groups[1].Value; continue; }
            Match row = Regex.Match(line, @"^\s+L\s*\d+ \(\s*(\d+)\)\s+([0-9.]+) \(");
            if (row.Success && atlas.Length > 0) result[(atlas, int.Parse(row.Groups[1].Value))] = double.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }
}
