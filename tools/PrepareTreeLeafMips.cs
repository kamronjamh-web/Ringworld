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

/// <summary>TR-7c-PV (preview only, nothing shipped): builds the distance-stable leaf mips of the three leaf atlases (TreeLeafMipBuilder), compresses them level by level into the import's format,
/// gates the result and writes ImageTextures, manifest.json and leaf_mips_table.md under res://.godot/ (it refuses any other output folder).</summary>
public partial class PrepareTreeLeafMips : RefCounted
{
    public const string ToolVersion = "tr7c-pv-1";
    private const string ReferenceOutput = "res://.godot/tr7c_lead/leaf_mips_reference_output.txt";

    private sealed class Atlas
    {
        public string Name = "", Path = "";
        public float Cutoff; public bool HasCutoff;
        public Texture2D Texture = null!;
        public readonly SortedSet<int> Variants = new();
        public readonly Dictionary<int, double[]> Usage = new();
    }

    public int Run(string outDir, string reportPath)
    {
        var failures = new List<string>();
        var openMisses = new List<string>();
        try
        {
            if (!outDir.StartsWith("res://.godot/")) throw new ArgumentException("output folder must be under res://.godot/: " + outDir);
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(outDir));
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            var atlases = Group(variants, failures);
            var manifest = new List<object>();
            var md = new StringBuilder();
            md.AppendLine("# TR-7c-PV leaf mip table (stable mips; per-variant passing area against level 0, usage weighted)");
            md.AppendLine();
            var reference = ParseReference();
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
                foreach (bool closed in new[] { false, true })
                {
                var sink = closed ? failures : openMisses;
                var built = TreeLeafMipBuilder.Build(rgba, size, cutoff, species, closed ? Codec : null);
                var again = TreeLeafMipBuilder.Build(rgba, size, cutoff, species, closed ? Codec : null);
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
                var decodedAlpha = new byte[levels + 1][];
                for (int l = 1; l <= levels; l++) decodedAlpha[l] = DecodeAlpha(levelBytes[l], size >> l);
                var texture = ImageTexture.CreateFromImage(output);
                string file = $"atlas_{atlas.Name}{(closed ? "_cl" : "")}.res";
                if (ResourceSaver.Save(texture, $"{outDir}/{file}") != Error.Ok) sink.Add($"{atlas.Name}: could not save {file}");
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
                    md.AppendLine($"L{l} | {s} | {Fmt(built1)} | {Fmt(half1)} | {Fmt(dec1)} | {Fmt(dhalf1)} | {string.Join(" ", built.Scales[l].Select(x => x.ToString("0.00")))}");
                    double mean = dec1.Average();
                    // TR.md "TR-7c-PV DXT5 note (2026-11-15)": two gates are bounded at the measured DXT5 floor (maple 8x8 species mean 3 %, pine 4x4 per variant 20 %).
                    double meanTolerance = atlas.Name == "maple" && s == 8 ? 0.03 : 0.02, variantTolerance4 = atlas.Name == "pine" ? 0.20 : 0.10;
                    if (s >= 8)
                    {
                        if (Math.Abs(mean - 1) > meanTolerance) sink.Add($"{atlas.Name} L{l} ({s}x{s}): species mean {mean:0.000} is off by more than {meanTolerance * 100:0} % after DXT5");
                        if (dec1.Any(r => Math.Abs(r - 1) > 0.05)) sink.Add($"{atlas.Name} L{l} ({s}x{s}): a variant is off by more than 5 % after DXT5 ({dec1.Min():0.000}..{dec1.Max():0.000})");
                    }
                    else if (s == 4 && dec1.Any(r => Math.Abs(r - 1) > variantTolerance4)) sink.Add($"{atlas.Name} L{l} (4x4): a variant is off by more than {variantTolerance4 * 100:0} % after DXT5 ({dec1.Min():0.000}..{dec1.Max():0.000})");
                    if (s >= 8 && reference.TryGetValue((atlas.Name, s), out double refMean) && Math.Abs(mean - refMean) > meanTolerance)
                        sink.Add($"{atlas.Name} L{l} ({s}x{s}): table mean {mean:0.000} differs from the reference table's {refMean:0.000} by more than 2 points");
                }
                if (decodedAlpha[levels][0] / 255.0 >= cutoff || built.Alpha[levels][0] / 255.0 >= cutoff) sink.Add($"{atlas.Name}: the 1x1 level passes the cutoff");
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
            File.WriteAllText(ProjectSettings.GlobalizePath($"{outDir}/leaf_mips_table.md"), md.ToString());
            File.WriteAllText(ProjectSettings.GlobalizePath($"{outDir}/manifest.json"),
                JsonSerializer.Serialize(new { tool_version = ToolVersion, atlases = manifest, failures, open_loop_gate_misses = openMisses }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(ProjectSettings.GlobalizePath(reportPath), md.ToString());
            if (atlases.Count != 3) failures.Add($"expected 3 leaf atlases, found {atlases.Count}");
            if (failures.Count > 0)
            {
                foreach (string f in failures) GD.PushError("PREPARE_TREE_LEAF_MIPS gate: " + f);
                GD.PushError($"PREPARE_TREE_LEAF_MIPS=FAIL {failures.Count} gate failures");
                return 1;
            }
            foreach (string m in openMisses) GD.Print("PREPARE_TREE_LEAF_MIPS open-loop gate miss (information): " + m);
            GD.Print("PREPARE_TREE_LEAF_MIPS=PASS (closed loop) atlases=" + atlases.Count + " out=" + outDir);
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_TREE_LEAF_MIPS=FAIL " + exception);
            return 1;
        }
    }

    private static byte[] Codec(byte[] alpha, int s) => DecodeAlpha(CompressLevel(new byte[s * s * 3], alpha, s), s);

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
    public static byte[] DecodeAlpha(byte[] dxt5, int s)
    {
        int p = Math.Max(s, 4);
        Image image = Image.CreateFromData(p, p, false, Image.Format.Dxt5, dxt5);
        image.Decompress();
        image.Convert(Image.Format.Rgba8);
        byte[] rgba = image.GetData();
        var alpha = new byte[s * s];
        for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) alpha[y * s + x] = rgba[(y * p + x) * 4 + 3];
        return alpha;
    }

    // ---- catalog grouping and usage -------------------------------------------------------------------------------------------------------------------

    private static List<Atlas> Group(IReadOnlyList<ImportedTreeVariant> variants, List<string> failures)
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
