using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-4 gate: level-B targets, bark share, coverage and AABB rules, determinism, wind attachments, the stored hash and the loader fallback.</summary>
public partial class TreeLodDecimationTest : RefCounted
{
    private static readonly HashSet<int> BestEffort = new() { 3, 4, 5, 6, 12, 13, 14 };

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public int Run()
    {
        try
        {
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            Require(variants.Count == ImportedTreeCatalog.VariantCount, "variant count");
            string root = ProjectSettings.GlobalizePath("res://");

            // catalog untouched: its bytes still match the manifest written when it was baked
            using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(root + "resources/trees/prepared/lolipop_catalog.manifest.json")))
            using (FileStream stream = File.OpenRead(root + "resources/trees/prepared/lolipop_catalog.res"))
                Require(Convert.ToHexString(SHA256.HashData(stream)) == manifest.RootElement.GetProperty("artifact_sha256").GetString(), "lolipop_catalog.res changed");

            // target of 0 triangles must be refused
            bool refused = false;
            try { TreeLodDecimator.Decimate(variants[3], 0); } catch (ArgumentOutOfRangeException) { refused = true; }
            Require(refused, "a target of 0 triangles was accepted");

            // determinism of the saved bytes: two independent decimations of a few variants, saved twice, compared byte for byte
            {
                var first = new Dictionary<int, TreeLodDecimator.Result>(); var second = new Dictionary<int, TreeLodDecimator.Result>();
                foreach (int index in new[] { 11, 14, 24 })
                {
                    first[index] = TreeLodDecimator.Decimate(variants[index], TreeLodDecimator.TargetFor(index));
                    second[index] = TreeLodDecimator.Decimate(variants[index], TreeLodDecimator.TargetFor(index));
                }
                string a = "res://.godot/tr4_det_a.res", b = "res://.godot/tr4_det_b.res";
                Require(ResourceSaver.Save(PackSubset(variants, first), a) == Error.Ok && ResourceSaver.Save(PackSubset(variants, second), b) == Error.Ok, "save determinism files");
                byte[] ba = File.ReadAllBytes(ProjectSettings.GlobalizePath(a)), bb = File.ReadAllBytes(ProjectSettings.GlobalizePath(b));
                int diff = -1;
                for (int i = 0; i < Math.Min(ba.Length, bb.Length) && diff < 0; i++) if (ba[i] != bb[i]) diff = i;
                if (diff < 0 && ba.Length != bb.Length) diff = Math.Min(ba.Length, bb.Length);
                Require(diff < 0, $"saved resources differ byte for byte (first difference at {diff}, sizes {ba.Length}/{bb.Length}); kept {a} {b}");
                foreach (string p in new[] { a, b }) File.Delete(ProjectSettings.GlobalizePath(p));
            }

            var results = new Dictionary<int, TreeLodDecimator.Result>();
            int decimated = 0, withinRule = 0, bestEffort = 0;
            var recorded = new Dictionary<int, JsonElement>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(root + "resources/trees/prepared/lolipop_render_lods.manifest.json")))
                foreach (JsonElement e in doc.RootElement.GetProperty("variants").EnumerateArray()) recorded[e.GetProperty("index").GetInt32()] = e.Clone();
            double sum = 0;
            foreach (ImportedTreeVariant v in variants)
            {
                int target = TreeLodDecimator.TargetFor(v.Index);
                var r = TreeLodDecimator.Decimate(v, target);
                results[v.Index] = r;
                sum += r.Triangles;
                string label = $"v{v.Index} {v.Name} ";
                Require(r.Mesh.GetSurfaceCount() == 2, label + "surface layout");
                if (r.IsSource)
                {
                    Require(r.SourceTriangles <= (int)(target * TreeLodDecimator.SourceFactor), label + "kept as source above 1.25x target");
                    continue;
                }
                decimated++;
                Require(r.Triangles <= target, label + $"{r.Triangles} triangles over target {target}");
                Require(r.BarkTriangles >= TreeLodDecimator.BarkMinTriangles && r.BarkTriangles <= target * TreeLodDecimator.BarkMaxShare,
                    label + $"bark {r.BarkTriangles} outside 24..40% of {target}");
                Require(r.AabbOk, label + "AABB outside source + 2%");
                if (r.MeanError <= TreeLodDecimator.MeanLimit && r.WorstError <= TreeLodDecimator.WorstLimit) withinRule++;
                else
                {
                    // TR-4 escalation: foliage that cannot reach the target within the rule is reported with its best value. Those variants are
                    // listed here, and may not get worse than the value in the committed manifest.
                    Require(BestEffort.Contains(v.Index), label + $"coverage error {r.MeanError:P1} mean / {r.WorstError:P1} worst breaks the rule");
                    JsonElement entry = recorded[v.Index];
                    Require(r.MeanError <= entry.GetProperty("coverage_mean_error").GetDouble() + .01 && r.WorstError <= entry.GetProperty("coverage_worst_error").GetDouble() + .01,
                        label + $"best-effort coverage got worse: {r.MeanError:P1}/{r.WorstError:P1}");
                    bestEffort++;
                }
                CheckMesh(v, r.Mesh, label);
            }
            Require(decimated >= 20, "too few decimated variants: " + decimated);

            // determinism: decimate again and compare serialized arrays; save twice and compare file bytes
            foreach (int index in new[] { 3, 12, 18, 24, 27, 9 })
            {
                var again = TreeLodDecimator.Decimate(variants[index], TreeLodDecimator.TargetFor(index));
                Require(ImportedTreeRenderLodCatalog.HashLod(again.Mesh) == ImportedTreeRenderLodCatalog.HashLod(results[index].Mesh),
                    $"v{index} decimation is not deterministic");
            }
            // committed resource: hashes match the catalog, meshes equal a fresh run, loader assigns the shared materials
            ImportedTreeRenderLodCatalog.ConfigureForDiagnostics(null);
            var stored = ResourceLoader.Load<PreparedTreeRenderLods>(ImportedTreeRenderLodCatalog.ResourcePath)
                ?? throw new InvalidOperationException("lolipop_render_lods.res is missing");
            Require(stored.VariantNames.Length == variants.Count && stored.Level == "B" && stored.ToolVersion == TreeLodDecimator.ToolVersion, "stored header");
            for (int i = 0; i < variants.Count; i++)
            {
                ImportedTreeVariant v = variants[i];
                Require(stored.VariantNames[i] == v.Name, $"stored name v{i}");
                Require(stored.SourceHashes[i] == ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)v.Lods[2]), $"stored source hash v{i}");
                Require((stored.IsSource[i] == 1) == results[i].IsSource, $"stored source flag v{i}");
                Mesh lod = ImportedTreeRenderLodCatalog.GetLod2b(v);
                if (results[i].IsSource) { Require(ReferenceEquals(lod, v.Lods[2]), $"v{i} source variant did not return the authored LOD2"); continue; }
                Require(!ReferenceEquals(lod, v.Lods[2]) && lod.GetSurfaceCount() == 2, $"v{i} loader returned the authored LOD2");
                for (int s = 0; s < 2; s++) Require(ReferenceEquals(lod.SurfaceGetMaterial(s), v.Lods[2].SurfaceGetMaterial(s)), $"v{i} surface {s} material is not the shared one");
                Require(ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)lod) == ImportedTreeRenderLodCatalog.HashLod(results[i].Mesh), $"stored v{i} differs from a fresh decimation; rerun prepare_tree_render_lods");
                CheckMesh(v, lod, $"stored v{i} ");
            }
            Require(ImportedTreeRenderLodCatalog.StaleCount == 0 && ImportedTreeRenderLodCatalog.WarningCount == 0, "loader warned on the committed resource");

            // stale hash: loader falls back to the authored LOD2 and warns once per cause
            var stale = (PreparedTreeRenderLods)stored.Duplicate(true);
            stale.SourceHashes = stale.SourceHashes.Select(h => new string('0', h.Length)).ToArray();
            ImportedTreeRenderLodCatalog.ConfigureForDiagnostics(null, stale);
            foreach (ImportedTreeVariant v in variants)
                Require(ReferenceEquals(ImportedTreeRenderLodCatalog.GetLod2b(v), v.Lods[2]), "stale hash did not fall back to the authored LOD2");
            Require(ImportedTreeRenderLodCatalog.StaleCount == variants.Count, "stale count");
            ImportedTreeRenderLodCatalog.ConfigureForDiagnostics("res://.godot/does_not_exist_render_lods.res");
            Require(ReferenceEquals(ImportedTreeRenderLodCatalog.GetLod2b(variants[3]), variants[3].Lods[2]) && ImportedTreeRenderLodCatalog.FallbackReason.Contains("missing"), "missing resource fallback");
            ImportedTreeRenderLodCatalog.ConfigureForDiagnostics(null);

            GD.Print($"TREE_LOD_DECIMATION_TEST=PASS decimated={decimated} source={variants.Count - decimated} mean_triangles_B={sum / variants.Count:F0} within_rule={withinRule} best_effort={bestEffort}");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("TREE_LOD_DECIMATION_TEST=FAIL " + exception.Message);
            return 1;
        }
    }

    private static PreparedTreeRenderLods PackSubset(IReadOnlyList<ImportedTreeVariant> variants, Dictionary<int, TreeLodDecimator.Result> results)
    {
        var res = new PreparedTreeRenderLods { ToolVersion = TreeLodDecimator.ToolVersion, Level = "B" };
        foreach (var (index, r) in results.OrderBy(kv => kv.Key))
        {
            res.VariantNames = res.VariantNames.Append(variants[index].Name).ToArray();
            res.SourceHashes = res.SourceHashes.Append(ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)variants[index].Lods[2])).ToArray();
            res.IsSource = res.IsSource.Append(r.IsSource ? 1 : 0).ToArray();
            ArrayMesh? mesh = null;
            if (!r.IsSource) { mesh = (ArrayMesh)r.Mesh.Duplicate(false); for (int s = 0; s < 2; s++) mesh.SurfaceSetMaterial(s, null); }
            res.Meshes.Add(mesh!);
        }
        return TreeLodDecimator.StampIds(res);
    }

    private static void CheckMesh(ImportedTreeVariant v, Mesh mesh, string label)
    {
        var am = (ArrayMesh)mesh;
        Require(am.GetSurfaceCount() == 2, label + "surface count");
        Require(((ShaderMaterial)am.SurfaceGetMaterial(0)).GetShaderParameter("tree_foliage_mode").AsInt32() == 0
            && ((ShaderMaterial)am.SurfaceGetMaterial(1)).GetShaderParameter("tree_foliage_mode").AsInt32() == 1, label + "bark then foliage");
        int foliage = 0, pinned = 0;
        ImportedTreePreparedParity.CheckWind(am, ref foliage, ref pinned);
        Require(foliage == 1 && pinned > 0, label + "no pinned foliage attachments");
        Aabb bounds = am.GetAabb(), source = v.Lods[2].GetAabb();
        var arrays = am.SurfaceGetArrays(1);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        float[] data = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
        Vector3 grow = source.Size * TreeLodDecimator.AabbGrow;
        Aabb limit = new(source.Position - grow, source.Size + grow * 2);
        Require(limit.Encloses(bounds), label + "AABB outside source + 2%");
        for (int i = 0; i < vertices.Length; i++)
        {
            if (data[i * 4 + 3] <= 0) continue;
            var anchor = new Vector3(data[i * 4], data[i * 4 + 1], data[i * 4 + 2]);
            Require(limit.HasPoint(anchor) && 1 / data[i * 4 + 3] < source.Size.Length() * 1.1f, label + "attachment out of range");
        }
        foreach (int surface in new[] { 0, 1 })
        {
            var a = am.SurfaceGetArrays(surface);
            int count = a[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
            foreach (int idx in a[(int)Mesh.ArrayType.Index].AsInt32Array()) Require(idx >= 0 && idx < count, label + "index out of range");
        }
    }
}
