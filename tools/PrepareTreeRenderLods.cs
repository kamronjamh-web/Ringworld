using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>Writes lolipop_render_lods.res (level B) and its manifest; levels A and C go under a scratch folder for the contact sheets.</summary>
public partial class PrepareTreeRenderLods : RefCounted
{
    public int Run(string outputB, string manifestPath, string levelDir, string tablePath)
    {
        try
        {
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            var table = new StringBuilder();
            table.AppendLine("| # | variant | class | target B | authored LOD2 (bark+foliage) | A (bark+foliage) | B (bark+foliage) | C (bark+foliage) | B mean err | B worst err | A mean/worst | C mean/worst | B scale |");
            table.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            var entries = new List<object>();
            var totals = new Dictionary<char, double>();
            double sourceTotal = 0;
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(levelDir));
            var resources = new Dictionary<char, PreparedTreeRenderLods>();
            foreach (char level in "ABC")
                resources[level] = new PreparedTreeRenderLods { ToolVersion = TreeLodDecimator.ToolVersion, Level = level.ToString() };
            foreach (ImportedTreeVariant v in variants)
            {
                int classTarget = TreeLodDecimator.TargetFor(v.Index);
                string hash = ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)v.Lods[2]);
                var results = new Dictionary<char, TreeLodDecimator.Result>();
                foreach (char level in "ABC")
                {
                    var r = TreeLodDecimator.Decimate(v, TreeLodDecimator.LevelTarget(classTarget, level));
                    results[level] = r;
                    totals[level] = totals.GetValueOrDefault(level) + r.Triangles;
                    var res = resources[level];
                    res.VariantNames = res.VariantNames.Append(v.Name).ToArray();
                    res.SourceHashes = res.SourceHashes.Append(hash).ToArray();
                    res.IsSource = res.IsSource.Append(r.IsSource ? 1 : 0).ToArray();
                    ArrayMesh? stored = null;
                    if (!r.IsSource)
                    {
                        stored = (ArrayMesh)r.Mesh.Duplicate(false);
                        for (int s = 0; s < stored.GetSurfaceCount(); s++) stored.SurfaceSetMaterial(s, null);
                    }
                    res.Meshes.Add(stored!);
                }
                var b = results['B'];
                sourceTotal += b.SourceTriangles;
                string Cell(char l) => results[l].IsSource ? $"{results[l].Triangles} source" : $"{results[l].Triangles} ({results[l].BarkTriangles}+{results[l].FoliageTriangles})";
                string Err(char l) => results[l].IsSource ? "-" : $"{results[l].MeanError * 100:F1}%/{results[l].WorstError * 100:F1}%";
                table.AppendLine($"| {v.Index} | {v.Name} | {TreeLodDecimator.ClassFor(v.Index)} | {classTarget} | {b.SourceTriangles} | {Cell('A')} | {Cell('B')} | {Cell('C')} | "
                    + $"{(b.IsSource ? "-" : (b.MeanError * 100).ToString("F1") + "%")} | {(b.IsSource ? "-" : (b.WorstError * 100).ToString("F1") + "%")} | {Err('A')} | {Err('C')} | {b.Scale:F2} |");
                entries.Add(new
                {
                    index = v.Index, name = v.Name, size_class = TreeLodDecimator.ClassFor(v.Index), target = classTarget, source_hash = hash,
                    source_triangles = b.SourceTriangles, mode = b.IsSource ? "source" : "decimated",
                    bark_triangles = b.BarkTriangles, foliage_triangles = b.FoliageTriangles,
                    coverage_mean_error = Math.Round(b.MeanError, 5), coverage_worst_error = Math.Round(b.WorstError, 5),
                    within_rule = b.IsSource || b.WithinRule, foliage_scale = Math.Round(b.Scale, 3), inner_bias = b.InnerBias, note = b.Note
                });
            }
            int n = variants.Count;
            table.AppendLine($"| mean (unweighted, {n} variants) | | | | {sourceTotal / n:F0} | {totals['A'] / n:F0} | {totals['B'] / n:F0} | {totals['C'] / n:F0} | | | | | |");
            File.WriteAllText(ProjectSettings.GlobalizePath(tablePath), table.ToString());
            foreach (char level in "AC")
                if (ResourceSaver.Save(TreeLodDecimator.StampIds(resources[level]), $"{levelDir}/lod2b_{level}.res") != Error.Ok) throw new IOException("save level " + level);
            if (ResourceSaver.Save(TreeLodDecimator.StampIds(resources['B']), outputB) != Error.Ok) throw new IOException("save " + outputB);
            var manifest = new
            {
                schema_version = PreparedTreeRenderLods.CurrentSchemaVersion, tool_version = TreeLodDecimator.ToolVersion, level = "B",
                engine_identity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity, resource_path = "resources/trees/prepared/lolipop_render_lods.res",
                simplifier = "SurfaceTool.GenerateLod (meshoptimizer) for bark; whole-component removal plus survivor scale for foliage",
                variants = entries
            };
            File.WriteAllText(ProjectSettings.GlobalizePath(manifestPath), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            GD.Print("PREPARE_TREE_RENDER_LODS=PASS variants=" + n);
            GD.Print(table.ToString());
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_TREE_RENDER_LODS=FAIL " + exception);
            return 1;
        }
    }
}
