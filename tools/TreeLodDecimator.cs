using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Offline LOD2b builder (TR-4, design C2/C3). Bark: drop thin components, then meshoptimizer through
/// SurfaceTool.GenerateLod. Foliage: whole leaf-cluster components are removed in a fixed hashed order and the
/// survivors are scaled about their own attachment point; the scale and the ordering bias are picked by a CPU
/// alpha-coverage rasteriser. Everything here is deterministic (no GetHashCode, no random, stable sorts).
/// </summary>
public static class TreeLodDecimator
{
    public const int ToolVersion = 1;
    public const float MaxScale = 1.6f;
    public const float SourceFactor = 1.25f;
    public const float BarkMaxShare = .4f;
    public const int BarkMinTriangles = 24;
    public const float MinBarkDiameterMeters = .2f;
    public const float MeanLimit = .05f, WorstLimit = .08f, AabbGrow = .02f;

    public sealed class Result
    {
        public ArrayMesh Mesh = null!;
        public bool IsSource;
        public int Target, BarkTriangles, FoliageTriangles, SourceTriangles;
        public float MeanError, WorstError, Scale = 1, InnerBias;
        public bool AabbOk = true;
        public string Note = "";
        public int Triangles => BarkTriangles + FoliageTriangles;
        public bool WithinRule => MeanError <= MeanLimit && WorstError <= WorstLimit && AabbOk;
    }

    /// <summary>Class target of design C2 (level B), by variant index.</summary>
    public static int TargetFor(int index) => ClassFor(index) switch { "large" => 900, "big" => 700, "medium" => 500, "small" => 250, _ => 120 };

    public static string ClassFor(int index) => index switch
    {
        3 or 4 or 5 or 18 or 19 or 20 => "large",
        15 or 16 or 17 => "big",
        6 or 7 or 8 or 21 or 22 or 23 or 12 => "medium",
        9 or 10 or 11 or 27 or 28 or 29 or 13 => "small",
        _ => "sapling"
    };

    public static int LevelTarget(int classTarget, char level) => level switch
    {
        'A' => (int)MathF.Round(classTarget * 1.6f), 'B' => classTarget, 'C' => (int)MathF.Round(classTarget * .6f),
        _ => throw new ArgumentException("level")
    };

    /// <summary>Godot names sub-resources with random ids on save; fixed ids make the saved file identical from run to run.</summary>
    public static PreparedTreeRenderLods StampIds(PreparedTreeRenderLods resource)
    {
        resource.ResourceSceneUniqueId = "RenderLods";
        for (int i = 0; i < resource.Meshes.Count; i++)
            if (resource.Meshes[i] != null) resource.Meshes[i].ResourceSceneUniqueId = "Lod2b" + i.ToString("D2");
        return resource;
    }

    // ---- geometry container ------------------------------------------------------------------------------------

    internal sealed class Geo
    {
        public Vector3[] V = Array.Empty<Vector3>();
        public Vector3[]? N; public float[]? Tan; public Color[]? Col; public Vector2[]? UV, UV2; public float[]? C0;
        public int[] I = Array.Empty<int>();
        public int Triangles => I.Length / 3;

        public static Geo FromArrays(Godot.Collections.Array a)
        {
            var g = new Geo { V = a[(int)Mesh.ArrayType.Vertex].AsVector3Array() };
            if (a[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil) g.N = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            if (a[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil) g.Tan = a[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            if (a[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil) g.Col = a[(int)Mesh.ArrayType.Color].AsColorArray();
            if (a[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil) g.UV = a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (a[(int)Mesh.ArrayType.TexUV2].VariantType != Variant.Type.Nil) g.UV2 = a[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
            if (a[(int)Mesh.ArrayType.Custom0].VariantType != Variant.Type.Nil) g.C0 = a[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            g.I = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                ? Enumerable.Range(0, g.V.Length).ToArray() : a[(int)Mesh.ArrayType.Index].AsInt32Array();
            return g;
        }

        /// <summary>Arrays for AddSurfaceFromArrays; Custom0 is never written (the wind bake adds it).</summary>
        public Godot.Collections.Array ToArrays()
        {
            var a = new Godot.Collections.Array();
            a.Resize((int)Mesh.ArrayType.Max);
            a[(int)Mesh.ArrayType.Vertex] = V;
            if (N != null) a[(int)Mesh.ArrayType.Normal] = N;
            if (Tan != null) a[(int)Mesh.ArrayType.Tangent] = Tan;
            if (Col != null) a[(int)Mesh.ArrayType.Color] = Col;
            if (UV != null) a[(int)Mesh.ArrayType.TexUV] = UV;
            if (UV2 != null) a[(int)Mesh.ArrayType.TexUV2] = UV2;
            a[(int)Mesh.ArrayType.Index] = I;
            return a;
        }

        public ArrayMesh ToMesh(Material? material)
        {
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, ToArrays());
            mesh.SurfaceSetMaterial(0, material);
            return mesh;
        }

        /// <summary>Keep the listed triangles (in the given order); vertices are compacted by first use, so the result is deterministic.</summary>
        public Geo Subset(IEnumerable<int> triangles, Func<int, Vector3>? position = null)
        {
            var remap = new Dictionary<int, int>();
            var order = new List<int>();
            var indices = new List<int>();
            foreach (int t in triangles)
                for (int k = 0; k < 3; k++)
                {
                    int old = I[t * 3 + k];
                    if (!remap.TryGetValue(old, out int n)) { n = order.Count; remap[old] = n; order.Add(old); }
                    indices.Add(n);
                }
            var g = new Geo { I = indices.ToArray() };
            g.V = order.Select(o => position?.Invoke(o) ?? V[o]).ToArray();
            if (N != null) g.N = order.Select(o => N[o]).ToArray();
            if (Col != null) g.Col = order.Select(o => Col[o]).ToArray();
            if (UV != null) g.UV = order.Select(o => UV[o]).ToArray();
            if (UV2 != null) g.UV2 = order.Select(o => UV2[o]).ToArray();
            if (Tan != null) { g.Tan = new float[order.Count * 4]; for (int i = 0; i < order.Count; i++) Array.Copy(Tan, order[i] * 4, g.Tan, i * 4, 4); }
            return g;
        }

        public Aabb Bounds()
        {
            if (V.Length == 0) return new Aabb();
            Vector3 lo = V[0], hi = V[0];
            foreach (Vector3 p in V) { lo = lo.Min(p); hi = hi.Max(p); }
            return new Aabb(lo, hi - lo);
        }
    }

    internal sealed class Component
    {
        public List<int> Triangles = new();
        public Vector3 Centroid, Anchor;
        public float Area, MaxExtent;
        public double Key;
    }

    /// <summary>Connected components by shared (position-welded) vertices, numbered by first triangle, so the order is stable.</summary>
    internal static List<Component> Components(Geo g)
    {
        int[] parent = Enumerable.Range(0, g.V.Length).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { int x = Find(a), y = Find(b); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        var welded = new Dictionary<Vector3I, int>();
        for (int i = 0; i < g.V.Length; i++)
        {
            Vector3 p = g.V[i] * 100000;
            var key = new Vector3I(Mathf.RoundToInt(p.X), Mathf.RoundToInt(p.Y), Mathf.RoundToInt(p.Z));
            if (welded.TryGetValue(key, out int o)) Union(i, o); else welded[key] = i;
        }
        for (int t = 0; t < g.Triangles; t++) { Union(g.I[t * 3], g.I[t * 3 + 1]); Union(g.I[t * 3], g.I[t * 3 + 2]); }
        var byRoot = new Dictionary<int, Component>();
        var list = new List<Component>();
        for (int t = 0; t < g.Triangles; t++)
        {
            int r = Find(g.I[t * 3]);
            if (!byRoot.TryGetValue(r, out Component? c)) { c = new Component(); byRoot[r] = c; list.Add(c); }
            c.Triangles.Add(t);
        }
        foreach (Component c in list)
        {
            var used = new HashSet<int>();
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            foreach (int t in c.Triangles)
            {
                Vector3 a = g.V[g.I[t * 3]], b = g.V[g.I[t * 3 + 1]], d = g.V[g.I[t * 3 + 2]];
                c.Area += (b - a).Cross(d - a).Length() * .5f;
                for (int k = 0; k < 3; k++) if (used.Add(g.I[t * 3 + k])) { Vector3 p = g.V[g.I[t * 3 + k]]; c.Centroid += p; lo = lo.Min(p); hi = hi.Max(p); }
            }
            c.Centroid /= used.Count;
            Vector3 ext = hi - lo;
            c.MaxExtent = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            c.Anchor = c.Centroid;
            if (g.C0 != null)
                foreach (int v in used.OrderBy(x => x))
                    if (g.C0[v * 4 + 3] > 0) { c.Anchor = new Vector3(g.C0[v * 4], g.C0[v * 4 + 1], g.C0[v * 4 + 2]); break; }
        }
        return list;
    }

    // ---- alpha coverage ---------------------------------------------------------------------------------------------

    internal sealed class Coverage
    {
        private const float PixelMeters = .21f;
        private const int Sub = 2;
        private readonly float _heightMeters, _cutoff;
        private readonly byte[] _alpha; private readonly int _tw, _th;
        private readonly List<(Vector3 Right, Vector3 Up)> _views = new();
        private readonly (float X0, float Y0, int W, int H)[] _grid;
        public readonly int[] SourceCovered;

        public Coverage(Geo sourceBark, Geo sourceFoliage, ShaderMaterial foliageMaterial, float heightMeters)
        {
            _heightMeters = heightMeters;
            _cutoff = foliageMaterial.GetShaderParameter("alpha_cutoff").AsSingle();
            var tex = (Texture2D)foliageMaterial.GetShaderParameter("albedo_texture").AsGodotObject();
            Image img = tex.GetImage();
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgba8);
            byte[] data = img.GetData();
            _tw = img.GetWidth(); _th = img.GetHeight();
            _alpha = new byte[_tw * _th];
            for (int i = 0; i < _alpha.Length; i++) _alpha[i] = data[i * 4 + 3];
            for (int k = 0; k < 8; k++) _views.Add(ViewBasis(k * 45f, 0));
            for (int k = 0; k < 4; k++) _views.Add(ViewBasis(22.5f + 90f * k, 30));
            _grid = new (float, float, int, int)[_views.Count];
            SourceCovered = new int[_views.Count];
            for (int v = 0; v < _views.Count; v++)
            {
                float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
                foreach (Geo g in new[] { sourceBark, sourceFoliage })
                    foreach (Vector3 p in g.V)
                    {
                        float x = p.Dot(_views[v].Right) * heightMeters, y = p.Dot(_views[v].Up) * heightMeters;
                        x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); y0 = MathF.Min(y0, y); y1 = MathF.Max(y1, y);
                    }
                float mx = (x1 - x0) * .2f + 1, my = (y1 - y0) * .2f + 1;
                x0 -= mx; y0 -= my;
                float cell = PixelMeters / Sub;
                _grid[v] = (x0, y0, (int)((x1 - x0 + 2 * mx) / cell) + 1, (int)((y1 - y0 + 2 * my) / cell) + 1);
                SourceCovered[v] = Count(v, sourceBark, sourceFoliage);
            }
        }

        private static (Vector3, Vector3) ViewBasis(float azimuthDeg, float elevationDeg)
        {
            float az = Mathf.DegToRad(azimuthDeg), el = Mathf.DegToRad(elevationDeg);
            Vector3 toCamera = new(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
            Vector3 dir = -toCamera;
            Vector3 right = dir.Cross(Vector3.Up).Normalized();
            return (right, right.Cross(dir).Normalized());
        }

        public int Count(int view, Geo bark, Geo foliage)
        {
            var (x0, y0, w, h) = _grid[view];
            float cell = PixelMeters / Sub;
            var bits = new bool[w * h];
            int n = 0;
            void Hit(int i) { if (!bits[i]) { bits[i] = true; n++; } }
            Raster(view, bark, Hit, x0, y0, w, h, cell, false);
            Raster(view, foliage, Hit, x0, y0, w, h, cell, true);
            return n;
        }

        public int Views => _views.Count;
        public int CellCount(int view) => _grid[view].W * _grid[view].H;
        public void Cells(int view, Geo g, bool alpha, Action<int> hit)
        {
            var (x0, y0, w, h) = _grid[view];
            Raster(view, g, hit, x0, y0, w, h, PixelMeters / Sub, alpha);
        }

        private void Raster(int view, Geo g, Action<int> hit, float x0, float y0, int w, int h, float cell, bool alpha)
        {
            var (right, up) = _views[view];
            var px = new float[g.V.Length]; var py = new float[g.V.Length];
            for (int i = 0; i < g.V.Length; i++)
            {
                px[i] = (g.V[i].Dot(right) * _heightMeters - x0) / cell;
                py[i] = (g.V[i].Dot(up) * _heightMeters - y0) / cell;
            }
            for (int t = 0; t < g.Triangles; t++)
            {
                int a = g.I[t * 3], b = g.I[t * 3 + 1], c = g.I[t * 3 + 2];
                float ax = px[a], ay = py[a], bx = px[b], by = py[b], cx = px[c], cy = py[c];
                float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (MathF.Abs(area) < 1e-9f) continue;
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        float sx = x + .5f, sy = y + .5f;
                        float w0 = ((bx - sx) * (cy - sy) - (cx - sx) * (by - sy)) / area;
                        float w1 = ((cx - sx) * (ay - sy) - (ax - sx) * (cy - sy)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        if (alpha && g.UV != null)
                        {
                            Vector2 uv = g.UV[a] * w0 + g.UV[b] * w1 + g.UV[c] * w2;
                            float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
                            int tx = Math.Min(_tw - 1, (int)(u * _tw)), ty = Math.Min(_th - 1, (int)(v * _th));
                            if (_alpha[ty * _tw + tx] < _cutoff * 255f) continue;
                        }
                        hit(y * w + x);
                    }
            }
        }

        public (float Mean, float Worst) Error(Geo bark, Geo foliage)
        {
            float sum = 0, worst = 0;
            for (int v = 0; v < _views.Count; v++)
            {
                float e = MathF.Abs(Count(v, bark, foliage) - SourceCovered[v]) / (float)SourceCovered[v];
                sum += e; worst = MathF.Max(worst, e);
            }
            return (sum / _views.Count, worst);
        }
    }

    // ---- public entry -------------------------------------------------------------------------------------------

    public static Result Decimate(ImportedTreeVariant variant, int target)
    {
        if (target < BarkMinTriangles * 2) throw new ArgumentOutOfRangeException(nameof(target), "decimation target too small: " + target);
        var source = (ArrayMesh)variant.Lods[2];
        if (source.GetSurfaceCount() != 2) throw new InvalidOperationException("LOD2 must have a bark and a foliage surface");
        var barkMaterial = (ShaderMaterial)source.SurfaceGetMaterial(0);
        var leafMaterial = (ShaderMaterial)source.SurfaceGetMaterial(1);
        if (barkMaterial.GetShaderParameter("tree_foliage_mode").AsInt32() != 0 || leafMaterial.GetShaderParameter("tree_foliage_mode").AsInt32() != 1)
            throw new InvalidOperationException("LOD2 surface order must be bark then foliage");
        Geo bark = Geo.FromArrays(source.SurfaceGetArrays(0)), leaf = Geo.FromArrays(source.SurfaceGetArrays(1));
        int sourceTriangles = bark.Triangles + leaf.Triangles;
        if (sourceTriangles <= (int)(target * SourceFactor))
            return new Result { Mesh = (ArrayMesh)source.Duplicate(false), IsSource = true, Target = target, SourceTriangles = sourceTriangles,
                BarkTriangles = bark.Triangles, FoliageTriangles = leaf.Triangles, Note = "source" };

        float heightMeters = variant.HeightMeters;
        var coverage = new Coverage(bark, leaf, leafMaterial, heightMeters);
        Aabb sourceBounds = source.GetAabb();
        Vector3 grow = sourceBounds.Size * AabbGrow;
        Aabb limit = new(sourceBounds.Position - grow, sourceBounds.Size + grow * 2);
        List<Component> comps = Components(leaf);
        foreach (Component c in comps) c.Key = HashUnit(c.Centroid);

        Candidate? best = null;
        var triedTargets = new HashSet<int>();
        foreach (float share in new[] { .25f, .1f, BarkMaxShare })
        {
            int barkTarget = Math.Clamp((int)MathF.Round(target * share), BarkMinTriangles, (int)(target * BarkMaxShare));
            if (!triedTargets.Add(barkTarget)) continue;
            Geo newBark; string note;
            try { newBark = DecimateBark(bark, heightMeters, barkTarget, (int)(target * BarkMaxShare), out note); }
            catch (InvalidOperationException) when (best != null) { continue; } // meshopt cannot reach this share; the earlier share stands
            Candidate c = BestFoliage(leaf, comps, newBark, coverage, limit, target - newBark.Triangles);
            c.Bark = newBark; c.Note = note + $"; bark share {share:P0}";
            if (best == null || c.Objective < best.Objective) best = c;
            if (best.Objective <= .85f) break;
        }
        if (best == null || best.Foliage == null) throw new InvalidOperationException("no foliage left for " + variant.Name);

        var barkMesh = best.Bark!.ToMesh(barkMaterial);
        var canopy = ImportedTreeWindAttachments.BakeCanopy(best.Foliage.ToMesh(leafMaterial), barkMesh);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, best.Bark.ToArrays());
        mesh.SurfaceSetMaterial(0, barkMaterial);
        var flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, canopy.SurfaceGetArrays(0), null, null, flags);
        mesh.SurfaceSetMaterial(1, leafMaterial);
        return new Result { Mesh = mesh, Target = target, SourceTriangles = sourceTriangles, BarkTriangles = best.Bark.Triangles,
            FoliageTriangles = best.Foliage.Triangles, MeanError = best.Mean, WorstError = best.Worst, Scale = best.Scale, InnerBias = best.Order,
            AabbOk = best.AabbOk, Note = best.Note };
    }

    private sealed class Candidate
    {
        public Geo? Foliage, Bark; public float Objective = float.MaxValue, Mean, Worst, Scale = 1, Order; public bool AabbOk; public string Note = "";
    }

    /// <summary>Tries the fixed orders (hash blended with an inner-first bias at 0, .5 and 1; plus a coverage-greedy order at three scales)
    /// and keeps the foliage whose coverage error is smallest.</summary>
    private static Candidate BestFoliage(Geo leaf, List<Component> comps, Geo newBark, Coverage coverage, Aabb limit, int leafTarget)
    {
        Vector2 axis = Vector2.Zero;
        foreach (Vector3 p in leaf.V) axis += new Vector2(p.X, p.Z);
        axis /= leaf.V.Length;
        float rMax = comps.Max(c => new Vector2(c.Centroid.X, c.Centroid.Z).DistanceTo(axis)) + 1e-6f;
        var orders = new List<(float Id, List<int> Order)>();
        foreach (float bias in new[] { 0f, .5f, 1f })
            orders.Add((bias, Enumerable.Range(0, comps.Count)
                .OrderBy(i => (1 - bias) * comps[i].Key + bias * (new Vector2(comps[i].Centroid.X, comps[i].Centroid.Z).DistanceTo(axis) / rMax))
                .ThenBy(i => i).ToList()));
        // Greedy orders list components in the order they are KEPT; the others in the order they are REMOVED.
        var kept = new List<bool[]>();
        foreach ((float _, List<int> order) in orders)
        {
            var k = new bool[comps.Count]; Array.Fill(k, true);
            int remaining = leaf.Triangles;
            foreach (int i in order) { if (remaining <= leafTarget) break; k[i] = false; remaining -= comps[i].Triangles.Count; }
            kept.Add(k);
        }
        var ids = orders.Select(o => o.Id).ToList();
        foreach (float gs in new[] { 1f, 1.3f, 1.6f }) { kept.Add(GreedyKept(leaf, comps, newBark, coverage, limit, leafTarget, gs)); ids.Add(10 + gs); }

        var best = new Candidate();
        for (int o = 0; o < kept.Count; o++)
        {
            if (!kept[o].Any(x => x)) continue;
            float Eval(float scale, out Geo candidate, out float mean, out float worst, out bool aabbOk)
            {
                candidate = Scaled(leaf, comps, kept[o], scale, limit);
                (mean, worst) = coverage.Error(newBark, candidate);
                aabbOk = limit.Encloses(Merge(newBark.Bounds(), candidate.Bounds()));
                return MathF.Max(mean / MeanLimit, worst / WorstLimit) + (aabbOk ? 0 : 100);
            }
            float scaleBest = 1, objBest = float.MaxValue;
            for (int k = 0; k <= 6; k++)
            {
                float s = 1 + k * .1f;
                float v = Eval(s, out _, out _, out _, out _);
                if (v < objBest) { objBest = v; scaleBest = s; }
            }
            float center = scaleBest;
            for (float s = MathF.Max(1, center - .06f); s <= MathF.Min(MaxScale, center + .06f) + 1e-4f; s += .02f)
            {
                float v = Eval(s, out _, out _, out _, out _);
                if (v < objBest) { objBest = v; scaleBest = s; }
            }
            float final = Eval(scaleBest, out Geo geo, out float m, out float w, out bool ok);
            if (final < best.Objective) best = new Candidate { Foliage = geo, Objective = final, Mean = m, Worst = w, Scale = scaleBest, Order = ids[o], AabbOk = ok };
        }
        return best;
    }

    /// <summary>Deterministic coverage-greedy selection: each step keeps the component that adds the most not-yet-covered source area over the
    /// 12 views (relative to each view's source area), at a fixed survivor scale, until the triangle budget is full.</summary>
    private static bool[] GreedyKept(Geo leaf, List<Component> comps, Geo newBark, Coverage coverage, Aabb limit, int leafTarget, float scale)
    {
        int views = coverage.Views;
        var covered = new bool[views][];
        for (int v = 0; v < views; v++)
        {
            bool[] bits = covered[v] = new bool[coverage.CellCount(v)];
            coverage.Cells(v, newBark, false, i => bits[i] = true);
        }
        var footprints = new int[comps.Count][][];
        for (int c = 0; c < comps.Count; c++)
        {
            var only = new bool[comps.Count]; only[c] = true;
            Geo piece = Scaled(leaf, comps, only, scale, limit);
            footprints[c] = new int[views][];
            for (int v = 0; v < views; v++)
            {
                var list = new List<int>();
                var seen = new HashSet<int>();
                coverage.Cells(v, piece, true, i => { if (seen.Add(i)) list.Add(i); });
                list.Sort();
                footprints[c][v] = list.ToArray();
            }
        }
        var kept = new bool[comps.Count];
        int budget = leafTarget;
        while (true)
        {
            int pick = -1; double bestGain = 0;
            for (int c = 0; c < comps.Count; c++)
            {
                if (kept[c] || comps[c].Triangles.Count > budget) continue;
                double gain = 0;
                for (int v = 0; v < views; v++)
                {
                    int fresh = 0; bool[] cov = covered[v];
                    foreach (int i in footprints[c][v]) if (!cov[i]) fresh++;
                    gain += fresh / (double)coverage.SourceCovered[v];
                }
                if (gain > bestGain + 1e-12) { bestGain = gain; pick = c; }
            }
            if (pick < 0) break;
            kept[pick] = true; budget -= comps[pick].Triangles.Count;
            for (int v = 0; v < views; v++) foreach (int i in footprints[pick][v]) covered[v][i] = true;
        }
        return kept;
    }

    private static Aabb Merge(Aabb a, Aabb b) => a.Merge(b);

    private static double HashUnit(Vector3 centroid)
    {
        uint h = 2166136261;
        foreach (int q in new[] { Mathf.RoundToInt(centroid.X * 1000), Mathf.RoundToInt(centroid.Y * 1000), Mathf.RoundToInt(centroid.Z * 1000) })
            for (int b = 0; b < 4; b++) { h ^= (uint)((q >> (b * 8)) & 0xFF); h *= 16777619; }
        h ^= h >> 15; h *= 2246822519; h ^= h >> 13;
        return h / 4294967296.0;
    }

    private static Geo Scaled(Geo leaf, List<Component> comps, bool[] kept, float scale, Aabb limit)
    {
        // Each vertex belongs to exactly one component (welded vertices share it), so a per-vertex anchor map is well defined.
        var anchor = new Dictionary<int, Vector3>();
        var factor = new Dictionary<int, float>();
        var tris = new List<int>();
        Vector3 lo = limit.Position, hi = limit.End;
        for (int c = 0; c < comps.Count; c++)
        {
            if (!kept[c]) continue;
            Vector3 a = comps[c].Anchor;
            // The factor is the same for every component unless a component would leave the AABB limit; then it stops short of it.
            float s = scale;
            foreach (int t in comps[c].Triangles)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 d = leaf.V[leaf.I[t * 3 + k]] - a;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (d[axis] > 1e-6f) s = MathF.Min(s, (hi[axis] - a[axis]) / d[axis] * .999f);
                        else if (d[axis] < -1e-6f) s = MathF.Min(s, (lo[axis] - a[axis]) / d[axis] * .999f);
                    }
                }
            s = MathF.Max(1f, s);
            foreach (int t in comps[c].Triangles)
            {
                tris.Add(t);
                for (int k = 0; k < 3; k++) { anchor[leaf.I[t * 3 + k]] = a; factor[leaf.I[t * 3 + k]] = s; }
            }
        }
        return leaf.Subset(tris, v => { Vector3 a = anchor[v]; return a + (leaf.V[v] - a) * factor[v]; });
    }

    private static Geo DecimateBark(Geo bark, float heightMeters, int target, int maxTarget, out string note)
    {
        note = "";
        List<Component> comps = Components(bark);
        int largest = 0;
        for (int i = 1; i < comps.Count; i++) if (comps[i].Area > comps[largest].Area) largest = i;
        var keep = new List<int>();
        var thin = new List<(float Diameter, int Index)>();
        for (int i = 0; i < comps.Count; i++)
        {
            float diameter = comps[i].Area / (MathF.PI * MathF.Max(comps[i].MaxExtent, 1e-6f)) * heightMeters;
            if (i != largest && diameter < MinBarkDiameterMeters) { thin.Add((diameter, i)); continue; }
            keep.AddRange(comps[i].Triangles);
        }
        // The bark keeps at least BarkMinTriangles: the thickest thin components come back first.
        foreach ((float _, int i) in thin.OrderByDescending(t => t.Diameter).ThenBy(t => t.Index).ToList())
        {
            if (keep.Count >= BarkMinTriangles) break;
            keep.AddRange(comps[i].Triangles); thin.RemoveAll(t => t.Index == i);
        }
        int dropped = thin.Count;
        keep.Sort();
        Geo g = bark.Subset(keep);
        note = $"bark -{dropped} thin comps";
        if (g.Triangles <= target) return g;
        int want = target;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var st = new SurfaceTool();
            st.CreateFrom(g.ToMesh(null), 0);
            int[] lod = st.GenerateLod(1.0f, want * 3);
            // meshopt may stop a few triangles under the request; the bark must keep at least BarkMinTriangles.
            if (lod.Length / 3 < BarkMinTriangles && want < maxTarget) { want = Math.Min(maxTarget, want + 2); continue; }
            if (lod.Length >= 3 && lod.Length / 3 <= Math.Max(target, want))
            {
                var tris = new List<int>();
                // GenerateLod returns indices into g's vertices; rebuild a Geo with those triangles.
                Geo withIndices = new() { V = g.V, N = g.N, Tan = g.Tan, Col = g.Col, UV = g.UV, UV2 = g.UV2, I = lod };
                for (int t = 0; t < withIndices.Triangles; t++) tris.Add(t);
                note += $"; meshopt {g.Triangles}->{withIndices.Triangles}";
                return withIndices.Subset(tris);
            }
            want = Math.Max(3, (int)(want * .85f));
        }
        throw new InvalidOperationException("bark simplification did not reach " + target);
    }
}
