using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7a: CPU, deterministic bake of the authored LOD2 into the pack's LOD3 card layout. Each card (largest-plane style grouping of the
/// LOD3 triangles) gets an affine UV-to-plane fit; the LOD2 is rasterised orthographically along the card normal into the card's UV region with
/// a depth buffer (S x S sub-samples per texel). Albedo = linear average of the hit texture times tint, alpha = covered fraction; normal = the
/// shading normal of one surface sample (centre sub-sample, else the nearest hit), in the card's tangent frame; both dilated; normal mips are
/// selected per card by error diffusion among the covered children, never averaged. Single-threaded per variant, no hash order or random, so the bytes repeat.</summary>
public static class TreeImpostorAtlasBaker
{
    public const int Size = PreparedTreeImpostorAtlas.LayerSize;
    public const int Sub = 3;
    private static readonly (int X, int Y)[] DilateOrder = { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1) };

    // ---- inputs, copied out of Godot objects on the main thread ----------------------------------------------------

    public sealed class Surface
    {
        public bool Cutout; public float Cutoff = .25f;
        public float TintR = 1, TintG = 1, TintB = 1, TintA = 1; // tint in linear light (the mesh shader's source_color uniform is converted from sRGB)
        public byte[] Tex = Array.Empty<byte>(); public int TexW, TexH; // RGBA8, sRGB bytes
    }

    public sealed class Input
    {
        public int Index; public string Name = "";
        public List<Surface> Surfaces = new();
        public List<Vector3> Pa = new(), Pb = new(), Pc = new(), Na = new(), Nb = new(), Nc = new();
        public List<Vector2> Ua = new(), Ub = new(), Uc = new();
        public List<int> Surf = new();
        // LOD3 triangles, unindexed
        public Vector3[] CardP = Array.Empty<Vector3>(), CardN = Array.Empty<Vector3>(); public Vector2[] CardUv = Array.Empty<Vector2>(); public Vector4[] CardT = Array.Empty<Vector4>();

        /// <summary>A copy without the surfaces selected by the predicate (used by the test to make a deliberately broken input).</summary>
        public Input Without(Func<Surface, bool> drop)
        {
            var copy = new Input { Index = Index, Name = Name, CardP = CardP, CardN = CardN, CardUv = CardUv, CardT = CardT };
            var map = new int[Surfaces.Count];
            for (int s = 0; s < Surfaces.Count; s++) { if (drop(Surfaces[s])) { map[s] = -1; continue; } map[s] = copy.Surfaces.Count; copy.Surfaces.Add(Surfaces[s]); }
            for (int t = 0; t < Pa.Count; t++)
            {
                if (map[Surf[t]] < 0) continue;
                copy.Pa.Add(Pa[t]); copy.Pb.Add(Pb[t]); copy.Pc.Add(Pc[t]); copy.Na.Add(Na[t]); copy.Nb.Add(Nb[t]); copy.Nc.Add(Nc[t]);
                copy.Ua.Add(Ua[t]); copy.Ub.Add(Ub[t]); copy.Uc.Add(Uc[t]); copy.Surf.Add(map[Surf[t]]);
            }
            return copy;
        }
    }

    private static float SrgbToLinear(float c) => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f);
    private static float LinearToSrgb(float c) { c = Math.Clamp(c, 0, 1); return c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f; }
    private static readonly float[] SrgbLut = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();

    public static Input Extract(ImportedTreeVariant variant)
    {
        var input = new Input { Index = variant.Index, Name = variant.Name };
        Mesh lod2 = variant.Lods[2];
        for (int s = 0; s < lod2.GetSurfaceCount(); s++)
        {
            var surface = new Surface();
            if (lod2.SurfaceGetMaterial(s) is ShaderMaterial m)
            {
                surface.Cutout = m.GetShaderParameter("use_alpha_cutout").AsBool();
                surface.Cutoff = m.GetShaderParameter("alpha_cutoff").AsSingle();
                Color tint = m.GetShaderParameter("albedo_tint").AsColor();
                surface.TintR = SrgbToLinear(tint.R); surface.TintG = SrgbToLinear(tint.G); surface.TintB = SrgbToLinear(tint.B); surface.TintA = tint.A;
                if (m.GetShaderParameter("albedo_texture").AsGodotObject() is Texture2D texture && texture.GetImage() is Image source && !source.IsEmpty())
                {
                    Image image = (Image)source.Duplicate();
                    if (image.IsCompressed()) image.Decompress();
                    image.Convert(Image.Format.Rgba8);
                    surface.Tex = image.GetData(); surface.TexW = image.GetWidth(); surface.TexH = image.GetHeight();
                }
            }
            if (surface.Tex.Length == 0) { surface.Tex = new byte[] { 255, 255, 255, 255 }; surface.TexW = surface.TexH = 1; }
            input.Surfaces.Add(surface);
            var a = lod2.SurfaceGetArrays(s);
            Vector3[] v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[]? n = a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector2[] uv = a[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? new Vector2[v.Length] : a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            int[] idx = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, v.Length).ToArray() : a[(int)Mesh.ArrayType.Index].AsInt32Array();
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                Vector3 face = (v[i1] - v[i0]).Cross(v[i2] - v[i0]).Normalized();
                input.Pa.Add(v[i0]); input.Pb.Add(v[i1]); input.Pc.Add(v[i2]);
                input.Na.Add(n != null ? n[i0] : face); input.Nb.Add(n != null ? n[i1] : face); input.Nc.Add(n != null ? n[i2] : face);
                input.Ua.Add(uv[i0]); input.Ub.Add(uv[i1]); input.Uc.Add(uv[i2]);
                input.Surf.Add(s);
            }
        }
        // LOD3 expanded like ImportedTreeImpostorCatalog.Expand (same defaults for missing channels)
        var cp = new List<Vector3>(); var cn = new List<Vector3>(); var cu = new List<Vector2>(); var ct = new List<Vector4>();
        Mesh lod3 = variant.Lods[3];
        for (int s = 0; s < lod3.GetSurfaceCount(); s++)
        {
            var a = lod3.SurfaceGetArrays(s);
            Vector3[] v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] n = a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? Array.Empty<Vector3>() : a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector2[] uv = a[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? Array.Empty<Vector2>() : a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            float[] tan = a[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.Nil ? Array.Empty<float>() : a[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            int[] idx = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, v.Length).ToArray() : a[(int)Mesh.ArrayType.Index].AsInt32Array();
            foreach (int i in idx)
            {
                cp.Add(v[i]); cn.Add(n.Length > i ? n[i] : Vector3.Up); cu.Add(uv.Length > i ? uv[i] : Vector2.Zero);
                ct.Add(tan.Length >= (i + 1) * 4 ? new Vector4(tan[i * 4], tan[i * 4 + 1], tan[i * 4 + 2], tan[i * 4 + 3]) : new Vector4(1, 0, 0, 1));
            }
        }
        input.CardP = cp.ToArray(); input.CardN = cn.ToArray(); input.CardUv = cu.ToArray(); input.CardT = ct.ToArray();
        return input;
    }

    // ---- cards ---------------------------------------------------------------------------------------------------------

    public sealed class Card
    {
        public List<int> Triangles = new(); // first vertex index (multiple of 3) in the LOD3 arrays
        public Vector3 View;                // direction towards the baked viewer
        public Vector3 Normal, Tangent, Binormal; // the shader's frame (normal_data, tangent_data)
        public float FrameSign = 1;         // +1 when the viewer is on the clockwise (front) side of the winding, else -1: the shader negates the frame on back faces
        public bool IsTop;
        public Vector3 E1, E2;              // in-plane axes, perpendicular to the plane normal
        public double M00, M01, M10, M11, B0, B1; // uv = M * (s, t) + b
        public double FitResidual;
        public Vector4 UvRect;              // min u, min v, max u, max v of the card vertices
    }

    public static List<Card> BuildCards(Input input)
    {
        int count = input.CardP.Length;
        var normals = new List<Vector3>(); var offsets = new List<float>(); var members = new List<List<int>>();
        for (int t = 0; t + 2 < count; t += 3)
        {
            Vector3 cross = (input.CardP[t + 1] - input.CardP[t]).Cross(input.CardP[t + 2] - input.CardP[t]);
            if (cross.Length() * .5f < 1e-9f) continue;
            Vector3 n = cross.Normalized();
            if (n.X < -1e-4f || (Mathf.Abs(n.X) <= 1e-4f && (n.Z < -1e-4f || (Mathf.Abs(n.Z) <= 1e-4f && n.Y < 0)))) n = -n;
            float d = n.Dot(input.CardP[t]);
            int group = -1;
            for (int g = 0; g < normals.Count; g++) if (normals[g].Dot(n) > .9995f && Mathf.Abs(offsets[g] - d) < .01f) { group = g; break; }
            if (group < 0) { normals.Add(n); offsets.Add(d); members.Add(new List<int>()); group = normals.Count - 1; }
            members[group].Add(t);
        }
        var cards = new List<Card>();
        for (int g = 0; g < normals.Count; g++)
        {
            List<int> tris = members[g];
            var parent = new int[tris.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var owner = new Dictionary<(int, int, int, int, int), int>();
            for (int i = 0; i < tris.Count; i++)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = input.CardP[tris[i] + k]; Vector2 uv = input.CardUv[tris[i] + k];
                    var key = ((int)MathF.Round(p.X * 1000), (int)MathF.Round(p.Y * 1000), (int)MathF.Round(p.Z * 1000), (int)MathF.Round(uv.X * 1000), (int)MathF.Round(uv.Y * 1000));
                    if (owner.TryGetValue(key, out int other)) parent[Find(i)] = Find(other); else owner[key] = i;
                }
            var order = new List<int>(); var byRoot = new Dictionary<int, Card>();
            for (int i = 0; i < tris.Count; i++)
            {
                int root = Find(i);
                if (!byRoot.TryGetValue(root, out Card? card)) { card = new Card(); byRoot[root] = card; cards.Add(card); }
                card.Triangles.Add(tris[i]);
            }
            foreach (Card card in cards.Skip(cards.Count - byRoot.Count)) Finish(card, normals[g], input);
        }
        return cards;
    }

    private static void Finish(Card card, Vector3 plane, Input input)
    {
        Vector3 vertexNormal = Vector3.Zero, tangent = Vector3.Zero, winding = Vector3.Zero; float w = 1;
        bool first = true;
        foreach (int t in card.Triangles)
        {
            Vector3 cross = (input.CardP[t + 1] - input.CardP[t]).Cross(input.CardP[t + 2] - input.CardP[t]);
            winding += -cross; // front (clockwise seen from the viewer) side: the reverse of the counter-clockwise normal
            for (int k = 0; k < 3; k++)
            {
                vertexNormal += input.CardN[t + k]; tangent += new Vector3(input.CardT[t + k].X, input.CardT[t + k].Y, input.CardT[t + k].Z);
                if (first) { w = input.CardT[t].W; first = false; }
            }
        }
        card.Normal = vertexNormal.LengthSquared() > 1e-12f ? vertexNormal.Normalized() : Vector3.Up;
        card.Tangent = tangent.LengthSquared() > 1e-12f ? tangent.Normalized() : Vector3.Right;
        card.Binormal = card.Normal.Cross(card.Tangent).Normalized() * w;
        card.IsTop = Mathf.Abs(plane.Y) > .7f;
        card.View = card.IsTop ? (plane.Y >= 0 ? plane : -plane) : (plane.Dot(card.Normal) >= 0 ? plane : -plane);
        card.FrameSign = winding.Dot(card.View) >= 0 ? 1 : -1;
        Vector3 reference = Mathf.Abs(plane.Y) < .9f ? Vector3.Up : Vector3.Right;
        card.E1 = reference.Cross(plane).Normalized();
        card.E2 = plane.Cross(card.E1).Normalized();
        // least-squares affine fit uv = M (s, t) + b over the card's vertices
        var rows = new List<(double S, double T, double U, double V)>();
        float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
        foreach (int t in card.Triangles)
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = input.CardP[t + k]; Vector2 uv = input.CardUv[t + k];
                rows.Add((p.Dot(card.E1), p.Dot(card.E2), uv.X, uv.Y));
                u0 = Math.Min(u0, uv.X); u1 = Math.Max(u1, uv.X); v0 = Math.Min(v0, uv.Y); v1 = Math.Max(v1, uv.Y);
            }
        card.UvRect = new Vector4(u0, v0, u1, v1);
        double[] su = Solve(rows, r => r.U), sv = Solve(rows, r => r.V);
        card.M00 = su[0]; card.M01 = su[1]; card.B0 = su[2]; card.M10 = sv[0]; card.M11 = sv[1]; card.B1 = sv[2];
        double worst = 0;
        foreach (var r in rows)
            worst = Math.Max(worst, Math.Max(Math.Abs(card.M00 * r.S + card.M01 * r.T + card.B0 - r.U), Math.Abs(card.M10 * r.S + card.M11 * r.T + card.B1 - r.V)));
        card.FitResidual = worst;
    }

    private static double[] Solve(List<(double S, double T, double U, double V)> rows, Func<(double S, double T, double U, double V), double> value)
    {
        double[,] a = new double[3, 4];
        foreach (var r in rows)
        {
            double[] x = { r.S, r.T, 1 }; double y = value(r);
            for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) a[i, j] += x[i] * x[j]; a[i, 3] += x[i] * y; }
        }
        for (int i = 0; i < 3; i++) a[i, i] += 1e-12; // keeps a degenerate (collinear) fit solvable
        for (int c = 0; c < 3; c++)
        {
            int pivot = c; for (int r = c + 1; r < 3; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
            for (int j = 0; j < 4; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
            for (int r = 0; r < 3; r++)
            {
                if (r == c) continue;
                double f = a[r, c] / a[c, c];
                for (int j = c; j < 4; j++) a[r, j] -= f * a[c, j];
            }
        }
        return new[] { a[0, 3] / a[0, 0], a[1, 3] / a[1, 1], a[2, 3] / a[2, 2] };
    }

    // ---- rasterisation ---------------------------------------------------------------------------------------------

    /// <summary>Per sub-sample result inside a card's texel box.</summary>
    private sealed class Buffer
    {
        public int X0, Y0, W, H; // texel box
        public float[] Depth = Array.Empty<float>(); public int[] Tri = Array.Empty<int>(); public float[] B0 = Array.Empty<float>(), B1 = Array.Empty<float>();
    }

    /// <summary>Texel owner map: for each texel the index of the first card whose UV triangles touch one of its sub-samples, else -1.</summary>
    public static int[] OwnerMap(Input input, List<Card> cards)
    {
        var owner = new int[Size * Size]; Array.Fill(owner, -1);
        for (int c = 0; c < cards.Count; c++)
            foreach (int t in cards[c].Triangles)
            {
                Vector2 a = input.CardUv[t] * Size * Sub, b = input.CardUv[t + 1] * Size * Sub, d = input.CardUv[t + 2] * Size * Sub;
                float area = (b.X - a.X) * (d.Y - a.Y) - (d.X - a.X) * (b.Y - a.Y);
                if (MathF.Abs(area) < 1e-9f) continue;
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, d.X)))), maxX = Math.Min(Size * Sub - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, d.X))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, d.Y)))), maxY = Math.Min(Size * Sub - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, d.Y))));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        float sx = x + .5f, sy = y + .5f;
                        float w0 = ((b.X - sx) * (d.Y - sy) - (d.X - sx) * (b.Y - sy)) / area, w1 = ((d.X - sx) * (a.Y - sy) - (a.X - sx) * (d.Y - sy)) / area, w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        int texel = (y / Sub) * Size + x / Sub;
                        if (owner[texel] < 0) owner[texel] = c;
                    }
            }
        return owner;
    }

    private static Buffer Raster(Input input, Card card, int cardIndex, int[] owner, int sub)
    {
        int x0 = Size, y0 = Size, x1 = -1, y1 = -1;
        for (int i = 0; i < owner.Length; i++)
            if (owner[i] == cardIndex) { int x = i % Size, y = i / Size; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        var buf = new Buffer { X0 = x0, Y0 = y0, W = Math.Max(0, x1 - x0 + 1), H = Math.Max(0, y1 - y0 + 1) };
        int sw = buf.W * sub, sh = buf.H * sub;
        buf.Depth = new float[sw * sh]; Array.Fill(buf.Depth, float.NegativeInfinity);
        buf.Tri = new int[sw * sh]; Array.Fill(buf.Tri, -1); buf.B0 = new float[sw * sh]; buf.B1 = new float[sw * sh];
        if (buf.W == 0) return buf;
        Vector3 e1 = card.E1, e2 = card.E2, view = card.View;
        for (int t = 0; t < input.Pa.Count; t++)
        {
            Vector3[] p = { input.Pa[t], input.Pb[t], input.Pc[t] };
            var px = new float[3]; var py = new float[3]; var pd = new float[3];
            for (int k = 0; k < 3; k++)
            {
                double s = p[k].Dot(e1), tt = p[k].Dot(e2);
                double u = card.M00 * s + card.M01 * tt + card.B0, v = card.M10 * s + card.M11 * tt + card.B1;
                px[k] = (float)(u * Size * sub - buf.X0 * sub); py[k] = (float)(v * Size * sub - buf.Y0 * sub); pd[k] = p[k].Dot(view);
            }
            float area = (px[1] - px[0]) * (py[2] - py[0]) - (px[2] - px[0]) * (py[1] - py[0]);
            if (MathF.Abs(area) < 1e-9f) continue;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(px[0], MathF.Min(px[1], px[2])))), maxX = Math.Min(sw - 1, (int)MathF.Ceiling(MathF.Max(px[0], MathF.Max(px[1], px[2]))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(py[0], MathF.Min(py[1], py[2])))), maxY = Math.Min(sh - 1, (int)MathF.Ceiling(MathF.Max(py[0], MathF.Max(py[1], py[2]))));
            Surface surface = input.Surfaces[input.Surf[t]];
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    int texel = (buf.Y0 + y / sub) * Size + buf.X0 + x / sub;
                    if (owner[texel] != cardIndex) continue;
                    float sx = x + .5f, sy = y + .5f;
                    float w0 = ((px[1] - sx) * (py[2] - sy) - (px[2] - sx) * (py[1] - sy)) / area, w1 = ((px[2] - sx) * (py[0] - sy) - (px[0] - sx) * (py[2] - sy)) / area, w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float depth = pd[0] * w0 + pd[1] * w1 + pd[2] * w2;
                    int i = y * sw + x;
                    if (depth <= buf.Depth[i]) continue;
                    if (surface.Cutout)
                    {
                        Vector2 uv = input.Ua[t] * w0 + input.Ub[t] * w1 + input.Uc[t] * w2;
                        if (SampleAlpha(surface, uv) < surface.Cutoff) continue;
                    }
                    buf.Depth[i] = depth; buf.Tri[i] = t; buf.B0[i] = w0; buf.B1[i] = w1;
                }
        }
        return buf;
    }

    private static int TexelIndex(Surface s, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        return (Math.Min(s.TexH - 1, (int)(v * s.TexH)) * s.TexW + Math.Min(s.TexW - 1, (int)(u * s.TexW))) * 4;
    }
    private static float SampleAlpha(Surface s, Vector2 uv) => s.Tex[TexelIndex(s, uv) + 3] / 255f * s.TintA;

    // ---- bake ---------------------------------------------------------------------------------------------------------

    public sealed class VariantBake
    {
        public int CardCount, TopCards, OwnedTexels, CoveredTexels;
        public double WorstFitResidual;
        public List<Card> Cards = new();
        public int[] Owner = Array.Empty<int>();
        public byte[] Albedo = new byte[Size * Size * 4];           // sRGB RGBA8, base level
        public byte[] NormalMips = new byte[PreparedTreeImpostorAtlas.NormalLayerBytes];
        public float[][] Coverage = Array.Empty<float[]>();         // per level, covered fraction 0..1
        public short[][] CardId = Array.Empty<short[]>();           // per level, the card whose frame decodes the texel's normal (-1 none)
    }

    public static VariantBake Bake(Input input)
    {
        var result = new VariantBake { Cards = BuildCards(input) };
        result.CardCount = result.Cards.Count;
        result.TopCards = result.Cards.Count(c => c.IsTop);
        result.WorstFitResidual = result.Cards.Count == 0 ? 0 : result.Cards.Max(c => c.FitResidual);
        int[] owner = result.Owner = OwnerMap(input, result.Cards);
        result.OwnedTexels = owner.Count(o => o >= 0);
        var nrm = new byte[Size * Size * 3]; var cover = new float[Size * Size]; var card = new short[Size * Size]; Array.Fill(card, (short)-1);
        var albedo = result.Albedo;
        for (int c = 0; c < result.Cards.Count; c++)
        {
            Card cd = result.Cards[c];
            Buffer buf = Raster(input, cd, c, owner, Sub);
            int sw = buf.W * Sub;
            for (int ty = 0; ty < buf.H; ty++)
                for (int tx = 0; tx < buf.W; tx++)
                {
                    int texel = (buf.Y0 + ty) * Size + buf.X0 + tx;
                    if (owner[texel] != c) continue;
                    card[texel] = (short)c;
                    int hits = 0; float r = 0, g = 0, b = 0, aSum = 0; int pick = -1; float pickDistance = float.MaxValue;
                    for (int sy = 0; sy < Sub; sy++)
                        for (int sx = 0; sx < Sub; sx++)
                        {
                            int i = (ty * Sub + sy) * sw + tx * Sub + sx; int tri = buf.Tri[i];
                            if (tri < 0) continue;
                            hits++;
                            Surface surface = input.Surfaces[input.Surf[tri]];
                            Vector2 uv = input.Ua[tri] * buf.B0[i] + input.Ub[tri] * buf.B1[i] + input.Uc[tri] * (1 - buf.B0[i] - buf.B1[i]);
                            int o = TexelIndex(surface, uv);
                            r += SrgbLut[surface.Tex[o]] * surface.TintR; g += SrgbLut[surface.Tex[o + 1]] * surface.TintG; b += SrgbLut[surface.Tex[o + 2]] * surface.TintB;
                            aSum += 1;
                            float dx = sx - (Sub - 1) * .5f, dy = sy - (Sub - 1) * .5f, dist = dx * dx + dy * dy;
                            if (dist < pickDistance - 1e-6f) { pickDistance = dist; pick = i; } // the centre sub-sample, else the nearest hit; scan order breaks ties
                        }
                    if (hits == 0) continue;
                    cover[texel] = hits / (float)(Sub * Sub);
                    albedo[texel * 4] = (byte)Math.Round(LinearToSrgb(r / hits) * 255); albedo[texel * 4 + 1] = (byte)Math.Round(LinearToSrgb(g / hits) * 255);
                    albedo[texel * 4 + 2] = (byte)Math.Round(LinearToSrgb(b / hits) * 255); albedo[texel * 4 + 3] = (byte)Math.Round(cover[texel] * 255);
                    int tp = buf.Tri[pick];
                    float wa = buf.B0[pick], wb = buf.B1[pick], wc = 1 - wa - wb;
                    Vector3 n = (input.Na[tp] * wa + input.Nb[tp] * wb + input.Nc[tp] * wc).Normalized();
                    Vector3 frontNormal = -(input.Pb[tp] - input.Pa[tp]).Cross(input.Pc[tp] - input.Pa[tp]); // Godot: clockwise is the front face
                    if (frontNormal.Dot(cd.View) < 0) n = -n; // the mesh shader flips the normal of a face seen from behind
                    float s = cd.FrameSign;
                    Vector3 stored = new Vector3(n.Dot(cd.Tangent), n.Dot(cd.Binormal), n.Dot(cd.Normal)) * s;
                    nrm[texel * 3] = Encode(stored.X); nrm[texel * 3 + 1] = Encode(stored.Y); nrm[texel * 3 + 2] = Encode(stored.Z);
                }
        }
        result.CoveredTexels = cover.Count(v => v > 0);
        Dilate(albedo, nrm, cover, card);
        BuildNormalMips(result, nrm, cover, card);
        return result;
    }

    private static byte Encode(float v) => (byte)Math.Clamp((int)MathF.Round((v * .5f + .5f) * 255f), 0, 255);

    private static void Dilate(byte[] albedo, byte[] nrm, float[] cover, short[] card)
    {
        var filled = new bool[Size * Size];
        for (int i = 0; i < filled.Length; i++) filled[i] = cover[i] > 0;
        for (int pass = 0; pass < 16; pass++)
        {
            var next = (bool[])filled.Clone();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    if (filled[i]) continue;
                    foreach ((int dx, int dy) in DilateOrder)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= Size || !filled[ny * Size + nx]) continue;
                        int j = ny * Size + nx;
                        albedo[i * 4] = albedo[j * 4]; albedo[i * 4 + 1] = albedo[j * 4 + 1]; albedo[i * 4 + 2] = albedo[j * 4 + 2]; albedo[i * 4 + 3] = 0;
                        nrm[i * 3] = nrm[j * 3]; nrm[i * 3 + 1] = nrm[j * 3 + 1]; nrm[i * 3 + 2] = nrm[j * 3 + 2]; card[i] = card[j];
                        next[i] = true; break;
                    }
                }
            filled = next;
        }
        for (int i = 0; i < filled.Length; i++)
            if (!filled[i]) { albedo[i * 4] = albedo[i * 4 + 1] = albedo[i * 4 + 2] = 128; albedo[i * 4 + 3] = 0; nrm[i * 3] = nrm[i * 3 + 1] = 128; nrm[i * 3 + 2] = 255; }
    }

    /// <summary>Level 0 is the baked normal; each higher texel takes the normal of one covered child of its most-covered child's card, chosen by per-card error diffusion
    /// on the six-axis clamped light response (integer arithmetic, first of TL, TR, BL, BR on a tie), so every stored normal is a surface normal and the mip keeps the sun response.</summary>
    private static void BuildNormalMips(VariantBake bake, byte[] nrm, float[] cover, short[] card)
    {
        int levels = PreparedTreeImpostorAtlas.MipCount;
        bake.Coverage = new float[levels][]; bake.CardId = new short[levels][];
        var rgb = new byte[levels][];
        bake.Coverage[0] = cover; bake.CardId[0] = card; rgb[0] = nrm;
        for (int l = 1; l < levels; l++)
        {
            int size = PreparedTreeImpostorAtlas.LevelSize(l), child = PreparedTreeImpostorAtlas.LevelSize(l - 1);
            bake.Coverage[l] = new float[size * size]; bake.CardId[l] = new short[size * size]; rgb[l] = new byte[size * size * 3];
            var error = new Dictionary<short, long[]>();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int best = -1; float bestCover = -1, sum = 0;
                    var childIndex = new int[4]; var weight = new long[4];
                    for (int k = 0; k < 4; k++)
                    {
                        int cx = x * 2 + (k & 1), cy = y * 2 + (k >> 1), ci = cy * child + cx;
                        float v = bake.Coverage[l - 1][ci]; sum += v;
                        childIndex[k] = ci; weight[k] = (long)Math.Round(v * Sub * Sub * (1 << (2 * (l - 1))));
                        if (v > bestCover + 1e-7f) { bestCover = v; best = ci; }
                    }
                    int i = y * size + x;
                    if (bestCover > 0)
                    {
                        // Error diffusion among the covered children of the most-covered child's card: pick the child whose clamped response to light along the six
                        // frame axes keeps the card's running error smallest, so the picked normals reproduce the coverage-weighted response over the card.
                        short blockCard = bake.CardId[l - 1][best];
                        var cand = new List<int>(4);
                        for (int k = 0; k < 4; k++) if (weight[k] > 0 && bake.CardId[l - 1][childIndex[k]] == blockCard) cand.Add(k);
                        if (cand.Count > 0)
                        {
                            if (!error.TryGetValue(blockCard, out long[] e)) error[blockCard] = e = new long[6];
                            var f = new long[cand.Count][];
                            long s = 0; var target = new long[6];
                            for (int c = 0; c < cand.Count; c++)
                            {
                                int ci = childIndex[cand[c]];
                                f[c] = new long[6];
                                for (int a = 0; a < 3; a++)
                                {
                                    long v = 2 * rgb[l - 1][ci * 3 + a] - 255;
                                    f[c][a * 2] = Math.Max(v, 0); f[c][a * 2 + 1] = Math.Max(-v, 0);
                                }
                                s += weight[cand[c]];
                                for (int a = 0; a < 6; a++) target[a] += weight[cand[c]] * f[c][a];
                            }
                            int pick = 0; long pickDist = long.MaxValue;
                            for (int c = 0; c < cand.Count; c++)
                            {
                                long d = 0;
                                for (int a = 0; a < 6; a++) { long t = e[a] + target[a] - s * f[c][a]; d += t * t; }
                                if (d < pickDist) { pickDist = d; pick = c; }
                            }
                            for (int a = 0; a < 6; a++) e[a] += target[a] - s * f[pick][a];
                            best = childIndex[cand[pick]];
                        }
                    }
                    bake.Coverage[l][i] = sum / 4; bake.CardId[l][i] = bake.CardId[l - 1][best];
                    rgb[l][i * 3] = rgb[l - 1][best * 3]; rgb[l][i * 3 + 1] = rgb[l - 1][best * 3 + 1]; rgb[l][i * 3 + 2] = rgb[l - 1][best * 3 + 2];
                }
        }
        for (int l = 0; l < levels; l++)
        {
            int size = PreparedTreeImpostorAtlas.LevelSize(l), offset = PreparedTreeImpostorAtlas.NormalLevelOffsetBytes(l);
            for (int i = 0; i < size * size; i++)
            {
                bake.NormalMips[offset + i * 4] = rgb[l][i * 3]; bake.NormalMips[offset + i * 4 + 1] = rgb[l][i * 3 + 1]; bake.NormalMips[offset + i * 4 + 2] = rgb[l][i * 3 + 2]; bake.NormalMips[offset + i * 4 + 3] = 255;
            }
        }
    }

    // ---- checks ---------------------------------------------------------------------------------------------------------

    public static readonly float[] SunElevations = { 90f, 30f, 8f };
    public static readonly float[] SunAzimuths = { 30f, 120f, 210f, 300f };

    /// <summary>Mean of max(n.L, 0) over the covered texels (weighted by coverage) of mip `level`, sun alone, averaged over four azimuths; the world normal is the one the shader reconstructs from the stored tangent-space normal.</summary>
    public static float SunResponse(VariantBake bake, int level, float elevationDegrees)
    {
        int size = PreparedTreeImpostorAtlas.LevelSize(level), offset = PreparedTreeImpostorAtlas.NormalLevelOffsetBytes(level);
        double total = 0, weight = 0;
        for (int i = 0; i < size * size; i++)
        {
            float cov = bake.Coverage[level][i]; short id = bake.CardId[level][i];
            if (cov <= 0 || id < 0) continue;
            Card c = bake.Cards[id];
            float x = bake.NormalMips[offset + i * 4] / 255f * 2 - 1, y = bake.NormalMips[offset + i * 4 + 1] / 255f * 2 - 1, z = bake.NormalMips[offset + i * 4 + 2] / 255f * 2 - 1;
            Vector3 n = (c.Tangent * x + c.Binormal * y + c.Normal * z) * c.FrameSign;
            n = n.Normalized();
            double sum = 0;
            foreach (float az in SunAzimuths)
            {
                float el = Mathf.DegToRad(elevationDegrees), a = Mathf.DegToRad(az);
                Vector3 l = new(MathF.Cos(el) * MathF.Cos(a), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(a));
                sum += Math.Max(n.Dot(l), 0);
            }
            total += cov * sum / SunAzimuths.Length; weight += cov;
        }
        return weight > 0 ? (float)(total / weight) : 0;
    }

    /// <summary>Independent coverage of the LOD2 silhouette from each card's view at a finer sub-sampling: per card the owned texels' coverage sum and the texel-wise intersection-over-union of (baked alpha at least half) against (silhouette at least half).</summary>
    public static (double BakedSum, double SilhouetteSum, double Iou, double ShiftIou)[] SilhouetteCheck(Input source, VariantBake bake, int sub = 5)
    {
        var result = new (double, double, double, double)[bake.Cards.Count];
        for (int c = 0; c < bake.Cards.Count; c++)
        {
            Buffer buf = Raster(source, bake.Cards[c], c, bake.Owner, sub);
            int sw = buf.W * sub; double baked = 0, silhouette = 0; int inter = 0, union = 0;
            var mask = new bool[buf.W * buf.H];
            for (int ty = 0; ty < buf.H; ty++)
                for (int tx = 0; tx < buf.W; tx++)
                {
                    int texel = (buf.Y0 + ty) * Size + buf.X0 + tx;
                    if (bake.Owner[texel] != c) continue;
                    mask[ty * buf.W + tx] = bake.Albedo[texel * 4 + 3] >= 128; // alpha / 255 >= .5
                    int hits = 0;
                    for (int sy = 0; sy < sub; sy++) for (int sx = 0; sx < sub; sx++) if (buf.Tri[(ty * sub + sy) * sw + tx * sub + sx] >= 0) hits++;
                    double sil = hits / (double)(sub * sub), alpha = bake.Albedo[texel * 4 + 3] / 255.0;
                    baked += alpha; silhouette += sil;
                    bool a = alpha >= .5, b = sil >= .5;
                    if (a && b) inter++; if (a || b) union++;
                }
            // sampling noise floor: the baked half-coverage mask against itself moved one texel along +x, -x, +y, -y (highest of the four)
            double shifted = 0;
            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int si = 0, su = 0;
                for (int ty = 0; ty < buf.H; ty++)
                    for (int tx = 0; tx < buf.W; tx++)
                    {
                        int sx = tx - dx, sy = ty - dy;
                        bool a = mask[ty * buf.W + tx], b = sx >= 0 && sy >= 0 && sx < buf.W && sy < buf.H && mask[sy * buf.W + sx];
                        if (a && b) si++; if (a || b) su++;
                    }
                shifted = Math.Max(shifted, su == 0 ? 1 : si / (double)su);
            }
            result[c] = (baked, silhouette, union == 0 ? 1 : inter / (double)union, shifted);
        }
        return result;
    }
}
