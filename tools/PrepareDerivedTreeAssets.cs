using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-5 (docs/work-orders/terrain-vegetation/T6.md, design A6): builds the derived tree forms (snags 30-35, fallen trees 36-39, stumps 40-42) from the prepared base catalog
/// (ImportedTreeCatalog.Load(), never the glTF) into resources/trees/prepared/lolipop_derived_catalog.res and its manifest. Bark surface (surface 0) only; forms are in the base
/// catalog's normalised space (root at the origin, height 1 = authored height). Deterministic: fixed loop order, no hash-set iteration. Writes only the two new files.
/// <see cref="Build"/> is shared with tree_catalog_append_test, which regenerates in process and compares mesh hashes with the manifest.
/// </summary>
public partial class PrepareDerivedTreeAssets : RefCounted
{
    /// <summary>3 (T6-11): fallen trees drop bark pieces no longer linked to the trunk (Geo.KeepNearAxis); snags, stumps, shrubs and the other logs are unchanged from version 2.</summary>
    public const int ToolVersion = 3;
    public const int LodF = 3;
    public const float LogClip = 0.55f;
    public const float StumpMeters = 0.6f;
    /// <summary>Fallen-tree near forms keep only bark within this many trunk radii of the root axis (broken-off limbs leave stubs).</summary>
    public const float LogReach = 3f;
    /// <summary>Tool version 3: two bark pieces of a fallen tree touch when a vertex lies within this many trunk radii of the other (or an edge crosses it); see Geo.KeepNearAxis.</summary>
    public const float LinkEps = .1f;
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_derived_catalog.res";
    public const string ManifestPath = "res://resources/trees/prepared/lolipop_derived_catalog.manifest.json";
    const string BaseManifestPath = "res://resources/trees/prepared/lolipop_catalog.manifest.json";

    public sealed class Result
    {
        public PreparedDerivedTreeCatalog Resource = null!;
        public int[,] Triangles = new int[0, 0]; // [form slot, lod]
        /// <summary>T6-7: the cluster rectangles each shrub look was cut from, per species (all rectangles in order, with the used count).</summary>
        public Dictionary<TreeSpecies, (List<ClusterRect> All, List<ClusterRect> Used)> Clusters = new();
    }

    public int Run(string output, string manifestPath)
    {
        try
        {
            Result built = Build(ImportedTreeCatalog.Load());
            PreparedDerivedTreeCatalog resource = built.Resource;
            if (ResourceSaver.Save(resource, output) != Error.Ok) throw new IOException("save " + output);
            string artifactSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath(output))));
            long bytes = new FileInfo(ProjectSettings.GlobalizePath(output)).Length;
            string baseSha = ReadBaseArtifactSha();
            var forms = new List<object>();
            for (int f = 0; f < resource.FormIndices.Length; f++)
            {
                var lods = new List<object>();
                for (int lod = 0; lod < PreparedDerivedTreeCatalog.LodsPerForm; lod++)
                {
                    ArrayMesh? mesh = resource.Meshes[f * PreparedDerivedTreeCatalog.LodsPerForm + lod];
                    if (mesh == null) continue;
                    lods.Add(new
                    {
                        lod = lod == LodF ? "F" : lod.ToString(), source_hash = resource.SourceHashes[f * PreparedDerivedTreeCatalog.LodsPerForm + lod],
                        mesh_hash = ImportedTreeRenderLodCatalog.HashLod(mesh), triangles = built.Triangles[f, lod]
                    });
                }
                forms.Add(new
                {
                    index = resource.FormIndices[f], form = ((TreeFormKind)resource.FormKinds[f]).ToString(), source_variant = resource.SourceVariants[f],
                    length_m = Math.Round(resource.LengthMeters[f], 4), trunk_radius_m = Math.Round(resource.TrunkRadiusMeters[f], 4), lods
                });
            }
            var shrubClusters = built.Clusters.OrderBy(c => (int)c.Key).Select(c => new
            {
                species = c.Key.ToString(), look = LookOf(c.Key).ToString(), atlas = AtlasPath(c.Key).Replace("res://", ""), usage_size = UsageSize, cutoff = ClusterCutoff, min_coverage = MinCoverage, max_coverage = MaxCoverage,
                found = c.Value.All.Count, used = c.Value.Used.Select(r => new { u0 = Math.Round(r.U0, 5), v0 = Math.Round(r.V0, 5), u1 = Math.Round(r.U1, 5), v1 = Math.Round(r.V1, 5), usage_pixels = r.UsagePixels, coverage = Math.Round(r.Coverage, 4) }).ToArray(),
                rejected = c.Value.All.Except(c.Value.Used).Select(r => new { u0 = Math.Round(r.U0, 5), v0 = Math.Round(r.V0, 5), u1 = Math.Round(r.U1, 5), v1 = Math.Round(r.V1, 5), coverage = Math.Round(r.Coverage, 4) }).ToArray()
            }).ToArray();
            var manifest = new
            {
                schema_version = PreparedDerivedTreeCatalog.CurrentSchemaVersion, tool_version = ToolVersion, catalog_version = resource.CatalogVersion, form_count = resource.FormIndices.Length,
                engine_identity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity, base_catalog_artifact_sha256 = baseSha,
                artifact_path = "resources/trees/prepared/lolipop_derived_catalog.res", artifact_sha256 = artifactSha, total_bytes = bytes, shrub_clusters = shrubClusters, forms
            };
            File.WriteAllText(ProjectSettings.GlobalizePath(manifestPath), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            GD.Print($"PREPARE_DERIVED_TREE_ASSETS=PASS forms={resource.FormIndices.Length} bytes={bytes}");
            for (int f = 0; f < resource.FormIndices.Length; f++)
                GD.Print($"  form {resource.FormIndices[f]} {(TreeFormKind)resource.FormKinds[f]} src={resource.SourceVariants[f]} tris=[{string.Join(",", Enumerable.Range(0, 4).Select(l => built.Triangles[f, l]))}]");
            foreach (int src in resource.SourceVariants.Distinct().OrderBy(s => s))
            {
                ImportedTreeVariant sv = ImportedTreeCatalog.Load()[src];
                GD.Print($"  source {src} {sv.Name} bark tris LOD0/1/2 = {string.Join("/", Enumerable.Range(0, 3).Select(l => Geo.Triangles((ArrayMesh)sv.Lods[l])))} (height {sv.HeightMeters:F2} m, trunk radius {sv.TrunkRadiusMeters:F3} m)");
            }
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_DERIVED_TREE_ASSETS=FAIL " + exception);
            return 1;
        }
    }

    public static string ReadBaseArtifactSha()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(BaseManifestPath)).TrimStart('﻿'));
        return doc.RootElement.GetProperty("artifact_sha256").GetString() ?? "";
    }

    /// <summary>The derived rows this tool builds: T6Derived rows of the production table that are a snag, fallen tree, stump or shrub (T6-7), in index order.</summary>
    public static IReadOnlyList<ImportedTreeSourceRow> FormRows()
        => ImportedTreeSourceTable.Rows.Where(row => row.Catalog == TreeSourceCatalog.T6Derived && row.Form is TreeFormKind.Snag or TreeFormKind.FallenTree or TreeFormKind.Stump or TreeFormKind.Shrub).ToArray();

    public static Result Build(IReadOnlyList<ImportedTreeVariant> catalog)
    {
        IReadOnlyList<ImportedTreeSourceRow> rows = FormRows();
        int lods = PreparedDerivedTreeCatalog.LodsPerForm;
        var res = new PreparedDerivedTreeCatalog { ToolVersion = ToolVersion, CatalogVersion = DerivedTreeCatalog.CatalogVersion };
        var tris = new int[rows.Count, lods];
        var hashes = new string[rows.Count * lods];
        Array.Fill(hashes, "");
        var meshes = new ArrayMesh?[rows.Count * lods];
        var length = new float[rows.Count];
        var radius = new float[rows.Count];
        var clusters = new Dictionary<TreeSpecies, (List<ClusterRect> All, List<ClusterRect> Used)>();
        for (int f = 0; f < rows.Count; f++)
        {
            ImportedTreeSourceRow row = rows[f];
            ImportedTreeVariant v = catalog[row.SourceBaseVariant];
            ArrayMesh Bark(int lod)
            {
                var source = (ArrayMesh)v.Lods[lod];
                if (source.SurfaceGetMaterial(0) is not ShaderMaterial m || m.GetShaderParameter("tree_foliage_mode").AsInt32() != 0)
                    throw new InvalidOperationException($"{v.Name} LOD{lod} surface 0 is not the bark surface");
                return source;
            }
            string Hash(int lod) => ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)v.Lods[lod]);
            void Put(int lod, ArrayMesh mesh, string hash)
            {
                meshes[f * lods + lod] = mesh; hashes[f * lods + lod] = hash; tris[f, lod] = Geo.Triangles(mesh);
            }
            if (row.Form == TreeFormKind.Shrub)
            {
                // T6-7: cards cut from the species' leaf-cluster atlas; the mesh is one foliage surface, LOD0 (slot 0) and LOD1 (slot 1). Source hash: the source variant's LOD0 (the loader checks it).
                if (!clusters.TryGetValue(v.Species, out var set))
                {
                    var atlas = Image.LoadFromFile(ProjectSettings.GlobalizePath(AtlasPath(v.Species))) ?? throw new IOException("atlas " + AtlasPath(v.Species));
                    List<ClusterRect> all = AllClusterRectangles(catalog, v.Species, atlas);
                    clusters[v.Species] = set = (all, all.Where(r => r.Coverage >= MinCoverage && r.Coverage <= MaxCoverage).Take(MaxClusters).ToList());
                }
                int first = rows.Where(r => r.Form == TreeFormKind.Shrub && r.Species == v.Species).Min(r => r.Index);
                string source0 = ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)v.Lods[0]);
                length[f] = 1f; radius[f] = 0f;
                Put(0, BuildShrub(LookOf(v.Species), row.Index, row.Index - first, 0, set.Used), source0);
                Put(1, BuildShrub(LookOf(v.Species), row.Index, row.Index - first, 1, set.Used), source0);
                continue;
            }
            radius[f] = v.TrunkRadiusMeters;
            Geo lod0 = Geo.FromSurface(Bark(0));
            float barkTop = lod0.P.Max(p => p.Y);
            float r0 = v.TrunkRadiusMeters / v.HeightMeters;
            switch (row.Form)
            {
                case TreeFormKind.Snag:
                    length[f] = v.HeightMeters;
                    for (int lod = 0; lod < 3; lod++) Put(lod, Geo.FromSurface(Bark(lod)).ToMesh(), Hash(lod));
                    Put(LodF, Geo.Pole(row.Index, r0, barkTop * .85f, float.PositiveInfinity).ToMesh(), Hash(0));
                    break;
                case TreeFormKind.FallenTree:
                    length[f] = LogClip * v.HeightMeters;
                    ArrayMesh near = Geo.FromSurface(Bark(1)).KeepNearAxis(LogReach * r0, LogClip, LinkEps * r0).Clip(LogClip).LieAlongX(LogClip).ToMesh();
                    ArrayMesh mid = Geo.FromSurface(Bark(2)).KeepNearAxis(LogReach * r0, LogClip, LinkEps * r0).Clip(LogClip).LieAlongX(LogClip).ToMesh();
                    Put(0, near, Hash(1));
                    Put(1, mid, Hash(2));
                    meshes[f * lods + 2] = mid; hashes[f * lods + 2] = Hash(2); tris[f, 2] = tris[f, 1]; // LOD2 shares LOD1's mesh (same sub-resource, no extra bytes)
                    Put(LodF, Geo.Pole(row.Index, r0, barkTop * .85f, LogClip).LieAlongX(LogClip).ToMesh(), Hash(0));
                    break;
                case TreeFormKind.Stump:
                    length[f] = StumpMeters;
                    Put(0, Geo.Stump(row.Index, r0, v.HeightMeters).ToMesh(), Hash(0));
                    break;
            }
        }
        res.FormIndices = rows.Select(r => r.Index).ToArray();
        res.FormKinds = rows.Select(r => (int)r.Form).ToArray();
        res.SourceVariants = rows.Select(r => r.SourceBaseVariant).ToArray();
        res.SourceHashes = hashes;
        res.LengthMeters = length;
        res.TrunkRadiusMeters = radius;
        for (int i = 0; i < meshes.Length; i++)
        {
            ArrayMesh? m = meshes[i];
            if (m != null && string.IsNullOrEmpty(m.ResourceSceneUniqueId)) m.ResourceSceneUniqueId = "Derived" + i.ToString("D3");
            res.Meshes.Add(m!);
        }
        res.ResourceSceneUniqueId = "DerivedTreeCatalog";
        return new Result { Resource = res, Triangles = tris, Clusters = clusters };
    }

    /// <summary>Plain vertex arrays of one surface, with the clipping, orientation and generated-pole helpers.</summary>
    sealed class Geo
    {
        public List<Vector3> P = new();
        public List<Vector3>? N;
        public List<float>? T;
        public List<Vector2>? U, U2;
        public List<float>? C0;
        public List<Color>? Col;
        public List<int> I = new();
        readonly List<int> _cut = new();

        public static int Triangles(ArrayMesh mesh) => mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;

        static Geo Like(Geo s) => new()
        {
            N = s.N == null ? null : new(), T = s.T == null ? null : new(), U = s.U == null ? null : new(), U2 = s.U2 == null ? null : new(),
            C0 = s.C0 == null ? null : new(), Col = s.Col == null ? null : new()
        };

        public static Geo FromSurface(ArrayMesh mesh)
        {
            Godot.Collections.Array a = mesh.SurfaceGetArrays(0);
            bool Has(Mesh.ArrayType t) => a[(int)t].VariantType != Variant.Type.Nil;
            if (!Has(Mesh.ArrayType.Index)) throw new InvalidOperationException("bark surface has no index array");
            var g = new Geo { P = a[(int)Mesh.ArrayType.Vertex].AsVector3Array().ToList(), I = a[(int)Mesh.ArrayType.Index].AsInt32Array().ToList() };
            if (Has(Mesh.ArrayType.Normal)) g.N = a[(int)Mesh.ArrayType.Normal].AsVector3Array().ToList();
            if (Has(Mesh.ArrayType.Tangent)) g.T = a[(int)Mesh.ArrayType.Tangent].AsFloat32Array().ToList();
            if (Has(Mesh.ArrayType.TexUV)) g.U = a[(int)Mesh.ArrayType.TexUV].AsVector2Array().ToList();
            if (Has(Mesh.ArrayType.TexUV2)) g.U2 = a[(int)Mesh.ArrayType.TexUV2].AsVector2Array().ToList();
            if (Has(Mesh.ArrayType.Custom0)) g.C0 = a[(int)Mesh.ArrayType.Custom0].AsFloat32Array().ToList();
            if (Has(Mesh.ArrayType.Color)) g.Col = a[(int)Mesh.ArrayType.Color].AsColorArray().ToList();
            return g;
        }

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = P.ToArray();
            if (N != null) arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
            if (T != null) arrays[(int)Mesh.ArrayType.Tangent] = T.ToArray();
            if (U != null) arrays[(int)Mesh.ArrayType.TexUV] = U.ToArray();
            if (U2 != null) arrays[(int)Mesh.ArrayType.TexUV2] = U2.ToArray();
            if (Col != null) arrays[(int)Mesh.ArrayType.Color] = Col.ToArray();
            if (C0 != null) arrays[(int)Mesh.ArrayType.Custom0] = C0.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = I.ToArray();
            var mesh = new ArrayMesh();
            var flags = C0 != null ? (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) : 0;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
            return mesh;
        }

        int Copy(Geo s, int i)
        {
            P.Add(s.P[i]);
            N?.Add(s.N![i]);
            if (T != null) for (int k = 0; k < 4; k++) T.Add(s.T![i * 4 + k]);
            U?.Add(s.U![i]); U2?.Add(s.U2![i]);
            if (C0 != null) for (int k = 0; k < 4; k++) C0.Add(s.C0![i * 4 + k]);
            Col?.Add(s.Col![i]);
            return P.Count - 1;
        }

        /// <summary>A vertex interpolated at t along a to b (normal and tangent re-normalised, tangent handedness from a).</summary>
        int Lerp(Geo s, int a, int b, float t)
        {
            P.Add(s.P[a].Lerp(s.P[b], t));
            if (N != null) N.Add(s.N![a].Lerp(s.N![b], t).Normalized());
            if (T != null)
            {
                Vector3 ta = new(s.T![a * 4], s.T[a * 4 + 1], s.T[a * 4 + 2]), tb = new(s.T[b * 4], s.T[b * 4 + 1], s.T[b * 4 + 2]);
                Vector3 x = ta.Lerp(tb, t).Normalized();
                T.Add(x.X); T.Add(x.Y); T.Add(x.Z); T.Add(s.T[a * 4 + 3]);
            }
            U?.Add(s.U![a].Lerp(s.U[b], t)); U2?.Add(s.U2![a].Lerp(s.U2[b], t));
            if (C0 != null) for (int k = 0; k < 4; k++) C0.Add(Mathf.Lerp(s.C0![a * 4 + k], s.C0[b * 4 + k], t));
            Col?.Add(s.Col![a].Lerp(s.Col[b], t));
            return P.Count - 1;
        }

        /// <summary>Keeps y &lt;= yc: whole triangles below, straddlers split exactly at the plane (Sutherland-Hodgman, fan), then a 12-gon cap over the cut.</summary>
        public Geo Clip(float yc)
        {
            Geo d = Like(this);
            var map = new Dictionary<int, int>();
            var edges = new Dictionary<(int, int), int>();
            int Keep(int i)
            {
                if (!map.TryGetValue(i, out int n)) { n = d.Copy(this, i); map[i] = n; }
                return n;
            }
            int Cut(int a, int b)
            {
                (int lo, int hi) = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue((lo, hi), out int n))
                {
                    float da = P[lo].Y - yc, db = P[hi].Y - yc;
                    n = d.Lerp(this, lo, hi, da / (da - db));
                    d.P[n] = new Vector3(d.P[n].X, yc, d.P[n].Z);
                    edges[(lo, hi)] = n; d._cut.Add(n);
                }
                return n;
            }
            var poly = new List<int>(4);
            for (int t = 0; t + 2 < I.Count; t += 3)
            {
                poly.Clear();
                for (int e = 0; e < 3; e++)
                {
                    int cur = I[t + e], next = I[t + (e + 1) % 3];
                    bool inC = P[cur].Y <= yc, inN = P[next].Y <= yc;
                    if (inC) poly.Add(Keep(cur));
                    if (inC != inN) poly.Add(Cut(cur, next));
                }
                for (int k = 1; k + 1 < poly.Count; k++) { d.I.Add(poly[0]); d.I.Add(poly[k]); d.I.Add(poly[k + 1]); }
            }
            d.Cap(yc);
            return d;
        }

        void Cap(float yc)
        {
            if (_cut.Count < 3) return;
            Vector3 centre = Vector3.Zero;
            foreach (int i in _cut) centre += P[i];
            centre /= _cut.Count;
            centre.Y = yc;
            float r = 0;
            foreach (int i in _cut) r += new Vector2(P[i].X - centre.X, P[i].Z - centre.Z).Length();
            r /= _cut.Count;
            Vector2 uv = Vector2.Zero, uv2 = Vector2.Zero;
            Color col = new(0, 0, 0, 0);
            foreach (int i in _cut) { if (U != null) uv += U[i]; if (U2 != null) uv2 += U2[i]; if (Col != null) col += Col[i]; }
            uv /= _cut.Count; uv2 /= _cut.Count; col /= _cut.Count;
            int first = P.Count;
            for (int k = 0; k <= 12; k++)
            {
                // 12-gon fan around the centroid; the bark texture sampled around the mean cut UV, so the cap reads as bark.
                float a = (k == 0 ? 0 : (k - 1)) * Mathf.Tau / 12f;
                Vector2 dir = k == 0 ? Vector2.Zero : new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                P.Add(centre + new Vector3(dir.X, 0, dir.Y) * r);
                N?.Add(Vector3.Up);
                if (T != null) { T.Add(1); T.Add(0); T.Add(0); T.Add(1); }
                U?.Add(uv + dir * .03f); U2?.Add(uv2 + dir * .03f);
                if (C0 != null) for (int c = 0; c < 4; c++) C0.Add(0);
                Col?.Add(col);
            }
            for (int k = 0; k < 12; k++) { I.Add(first); I.Add(first + 1 + k); I.Add(first + 1 + (k + 1) % 12); }
        }

        /// <summary>Rotates model +Y to +X and moves the axis point y = clip / 2 to the origin: (x, y, z) -> (y - c, -x, z).</summary>
        public Geo LieAlongX(float clip)
        {
            float c = clip * .5f;
            for (int i = 0; i < P.Count; i++)
            {
                P[i] = new Vector3(P[i].Y - c, -P[i].X, P[i].Z);
                if (N != null) N[i] = new Vector3(N[i].Y, -N[i].X, N[i].Z);
                if (T != null) { float tx = T[i * 4], ty = T[i * 4 + 1]; T[i * 4] = ty; T[i * 4 + 1] = -tx; }
            }
            return this;
        }

        /// <summary>Generated stump: 10-sided trunk of radius r from y = 0.15 m up, flaring to 1.3 r at y = 0 and straight down to a buried base at -0.15 m; the top ring (0.6 m) has each vertex jittered by a fixed integer hash of (form index, vertex) in [-0.06, +0.04] m (a broken top), closed by a 10-gon fan. UVs wrap into the bark texture like the LODF pole.</summary>
        public static Geo Stump(int formIndex, float r, float heightMeters)
        {
            var g = new Geo { N = new(), T = new(), U = new(), U2 = new() };
            float m = 1f / heightMeters; // metres to normalised units
            float[] ys = { -.15f * m, 0, .15f * m, .6f * m };
            float[] rs = { 1.3f * r, 1.3f * r, r, r };
            int[,] ring = new int[4, 11];
            float[] topY = new float[10];
            for (int k = 0; k < 10; k++)
            {
                uint h = ((uint)formIndex * 2654435761u) ^ ((uint)(k + 1) * 40503u * 2246822519u); h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                topY[k] = ys[3] + (-.06f + .10f * ((h >> 8) & 0xFFFFu) / 65535f) * m;
            }
            for (int j = 0; j < 4; j++)
                for (int k = 0; k <= 10; k++)
                {
                    float a = k * Mathf.Tau / 10f;
                    var radial = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    float y = j == 3 ? topY[k % 10] : ys[j];
                    int below = Math.Min(j, 2), above = below + 1;
                    float slope = (rs[below] - rs[above]) / (ys[above] - ys[below]);
                    g.Vert(new Vector3(radial.X * rs[j], y, radial.Z * rs[j]), new Vector3(radial.X, slope, radial.Z).Normalized(), new Vector3(-radial.Z, 0, radial.X),
                        new Vector2(.1f + .8f * k / 10f, .1f + .8f * (ys[j] - ys[0]) / (ys[3] - ys[0])));
                    ring[j, k] = g.P.Count - 1;
                }
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 10; k++)
                {
                    Vector3 outward = g.N![ring[j, k]] + g.N[ring[j, k + 1]];
                    g.Tri(ring[j, k], ring[j, k + 1], ring[j + 1, k], outward);
                    g.Tri(ring[j, k + 1], ring[j + 1, k + 1], ring[j + 1, k], outward);
                }
            float cy = topY.Average();
            int centre = g.Vert(new Vector3(0, cy, 0), Vector3.Up, Vector3.Right, new Vector2(.5f, .5f));
            int[] rim = new int[10];
            for (int k = 0; k < 10; k++)
            {
                float a = k * Mathf.Tau / 10f;
                rim[k] = g.Vert(new Vector3(Mathf.Cos(a) * r, topY[k], Mathf.Sin(a) * r), Vector3.Up, Vector3.Right, new Vector2(.5f + .1f * Mathf.Cos(a), .5f + .1f * Mathf.Sin(a)));
            }
            for (int k = 0; k < 10; k++) g.Tri(centre, rim[k], rim[(k + 1) % 10], Vector3.Up);
            return g;
        }

        /// <summary>Drops every triangle with a vertex farther than radius (horizontal, from the vertical axis through the root) from that axis, or wholly above yClip (the Clip that follows would
        /// drop it anyway); unused vertices are dropped by that Clip. Tool version 3 (T6-11): the bark is a set of separate shells (trunk, branch segments) that touch or pierce one another; a
        /// branch segment whose parent segment the radius removed would float beside the log (maple log 36 had a 6-triangle piece about 0.9 m off the trunk at LOD1/2). Of the pieces left
        /// (welded connected components), only those linked to the trunk piece (the largest) through a chain of touching pieces are kept; two pieces touch when an edge of one crosses a
        /// triangle of the other or a vertex lies within linkEps of it.</summary>
        public Geo KeepNearAxis(float radius, float yClip, float linkEps)
        {
            float r2 = radius * radius;
            var kept = new List<int>();
            for (int t = 0; t + 2 < I.Count; t += 3)
                if (I.Skip(t).Take(3).All(i => P[i].X * P[i].X + P[i].Z * P[i].Z <= r2) && I.Skip(t).Take(3).Any(i => P[i].Y <= yClip)) { kept.Add(I[t]); kept.Add(I[t + 1]); kept.Add(I[t + 2]); }
            I = KeepLinkedToTrunk(kept, linkEps);
            return this;
        }

        /// <summary>The triangles of <paramref name="tris"/> whose piece is linked to the largest piece (see <see cref="KeepNearAxis"/>). Deterministic: pieces in first-triangle order.</summary>
        List<int> KeepLinkedToTrunk(List<int> tris, float eps)
        {
            // Weld by position (1e-5 grid) and label pieces with a union-find over the triangles.
            var first = new Dictionary<(long, long, long), int>();
            var parent = new int[P.Count];
            for (int i = 0; i < P.Count; i++)
            {
                var key = ((long)Math.Round(P[i].X * 1e5), (long)Math.Round(P[i].Y * 1e5), (long)Math.Round(P[i].Z * 1e5));
                if (!first.TryGetValue(key, out int w)) { w = i; first[key] = i; }
                parent[i] = w;
            }
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = Find(tris[t]);
                for (int e = 1; e < 3; e++) { int b = Find(tris[t + e]); if (b != a) parent[b] = a; }
            }
            var pieceOfTri = new int[tris.Count / 3];
            var order = new List<int>();
            var members = new Dictionary<int, List<int>>();
            for (int t = 0; t < pieceOfTri.Length; t++)
            {
                int piece = pieceOfTri[t] = Find(tris[t * 3]);
                if (!members.TryGetValue(piece, out List<int>? list)) { members[piece] = list = new List<int>(); order.Add(piece); }
                list.Add(t);
            }
            if (order.Count <= 1) return tris;
            var boxes = new Dictionary<int, Aabb>();
            foreach (int piece in order)
            {
                var box = new Aabb(P[tris[members[piece][0] * 3]], Vector3.Zero);
                foreach (int t in members[piece]) for (int e = 0; e < 3; e++) box = box.Expand(P[tris[t * 3 + e]]);
                boxes[piece] = box.Grow(eps);
            }
            bool Crosses(int from, int to)
            {
                foreach (int t in members[from])
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 u = P[tris[t * 3 + e]], v = P[tris[t * 3 + (e + 1) % 3]];
                        foreach (int s in members[to])
                        {
                            Vector3 a = P[tris[s * 3]], b = P[tris[s * 3 + 1]], c = P[tris[s * 3 + 2]];
                            if (Geometry3D.SegmentIntersectsTriangle(u, v, a, b, c).VariantType != Variant.Type.Nil) return true;
                            if (u.DistanceTo(ClosestOnTriangle(u, a, b, c)) <= eps) return true;
                        }
                    }
                return false;
            }
            int trunk = order.OrderByDescending(piece => members[piece].Count).First(); // OrderBy is stable: ties keep first-triangle order
            var linked = new List<int> { trunk };
            var isLinked = new HashSet<int> { trunk };
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (int piece in order)
                {
                    if (isLinked.Contains(piece)) continue;
                    foreach (int other in linked)
                        if (boxes[piece].Intersects(boxes[other]) && (Crosses(piece, other) || Crosses(other, piece)))
                        {
                            linked.Add(piece); isLinked.Add(piece); changed = true; break;
                        }
                }
            }
            var result = new List<int>(tris.Count);
            for (int t = 0; t < pieceOfTri.Length; t++) if (isLinked.Contains(pieceOfTri[t])) { result.Add(tris[t * 3]); result.Add(tris[t * 3 + 1]); result.Add(tris[t * 3 + 2]); }
            return result;
        }

        /// <summary>The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b; float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c; float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // ---- generated far pole (LODF) ------------------------------------------------------------------------------

        void Tri(int a, int b, int c, Vector3 outward)
        {
            Vector3 w = (P[c] - P[a]).Cross(P[b] - P[a]);
            if (w.Dot(outward) < 0) (b, c) = (c, b);
            I.Add(a); I.Add(b); I.Add(c);
        }

        int Vert(Vector3 p, Vector3 n, Vector3 tangent, Vector2 uv)
        {
            P.Add(p); N!.Add(n);
            T!.Add(tangent.X); T.Add(tangent.Y); T.Add(tangent.Z); T.Add(1);
            U!.Add(uv); U2!.Add(uv);
            return P.Count - 1;
        }

        /// <summary>8-sided tapered pole from the root to yTop (radius r0 down to 0.15 r0 at yTop; yClip below yTop ends it early with a fan cap) with up to three 4-sided stubs of length 0.08.</summary>
        public static Geo Pole(int formIndex, float r0, float yTop, float yClip)
        {
            var g = new Geo { N = new(), T = new(), U = new(), U2 = new() };
            float rTop = r0 * .15f;
            float yEnd = Math.Min(yTop, yClip);
            float R(float y) => r0 + (rTop - r0) * (y / yTop);
            float rEnd = R(yEnd);
            float slope = (r0 - rTop) / yTop;
            int[,] ring = new int[2, 9];
            for (int j = 0; j < 2; j++)
                for (int k = 0; k <= 8; k++)
                {
                    float a = k * Mathf.Tau / 8f;
                    var radial = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    float y = j == 0 ? 0 : yEnd, r = j == 0 ? r0 : rEnd;
                    ring[j, k] = g.Vert(new Vector3(radial.X * r, y, radial.Z * r), new Vector3(radial.X, slope, radial.Z).Normalized(), new Vector3(-radial.Z, 0, radial.X),
                        new Vector2(.1f + .8f * k / 8f, .1f + .8f * (y / yTop)));
                }
            for (int k = 0; k < 8; k++)
            {
                Vector3 outward = g.N![ring[0, k]] + g.N[ring[0, k + 1]];
                g.Tri(ring[0, k], ring[0, k + 1], ring[1, k], outward);
                g.Tri(ring[0, k + 1], ring[1, k + 1], ring[1, k], outward);
            }
            for (int end = 0; end < 2; end++) // bottom (root) and top caps
            {
                float y = end == 0 ? 0 : yEnd, r = end == 0 ? r0 : rEnd;
                Vector3 n = end == 0 ? Vector3.Down : Vector3.Up;
                int centre = g.Vert(new Vector3(0, y, 0), n, Vector3.Right, new Vector2(.5f, .5f));
                int[] rim = new int[8];
                for (int k = 0; k < 8; k++)
                {
                    float a = k * Mathf.Tau / 8f;
                    rim[k] = g.Vert(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r), n, Vector3.Right, new Vector2(.5f + .1f * Mathf.Cos(a), .5f + .1f * Mathf.Sin(a)));
                }
                for (int k = 0; k < 8; k++) g.Tri(centre, rim[k], rim[(k + 1) % 8], n);
            }
            uint h = (uint)formIndex * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            float[] heights = { .50f, .62f, .74f };
            for (int s = 0; s < 3; s++)
            {
                if (heights[s] >= yEnd) continue;
                float ang = ((h >> (8 * s)) & 255u) / 256f * Mathf.Tau;
                g.Stub(heights[s], R(heights[s]), ang);
            }
            return g;
        }

        void Stub(float y, float poleRadius, float angle)
        {
            const float Length = .08f;
            Vector3 d = new Vector3(Mathf.Cos(angle) * .94f, .34f, Mathf.Sin(angle) * .94f).Normalized();
            Vector3 u = Vector3.Up.Cross(d).Normalized(), v = d.Cross(u);
            Vector3 c0 = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (poleRadius * .6f) + new Vector3(0, y, 0), c1 = c0 + d * Length;
            float w0 = poleRadius * .5f, w1 = w0 * .25f;
            int[,] ring = new int[2, 5];
            for (int j = 0; j < 2; j++)
                for (int k = 0; k <= 4; k++)
                {
                    float a = Mathf.Pi * .25f + k * Mathf.Pi * .5f;
                    Vector3 radial = u * Mathf.Cos(a) + v * Mathf.Sin(a);
                    ring[j, k] = Vert((j == 0 ? c0 : c1) + radial * (j == 0 ? w0 : w1) * 1.4142f, radial, d, new Vector2(.1f + .8f * k / 4f, j == 0 ? .5f : .6f));
                }
            for (int k = 0; k < 4; k++)
            {
                Vector3 outward = N![ring[0, k]] + N[ring[0, k + 1]];
                Tri(ring[0, k], ring[0, k + 1], ring[1, k], outward);
                Tri(ring[0, k + 1], ring[1, k + 1], ring[1, k], outward);
            }
            int tip = Vert(c1, d, u, new Vector2(.5f, .6f));
            for (int k = 0; k < 4; k++) Tri(tip, ring[1, k], ring[1, k + 1], d);
        }
    }
}
