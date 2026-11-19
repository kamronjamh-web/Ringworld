using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-7 (docs/work-orders/terrain-vegetation/T6.md, design A8): the shrub forms (indices 43-54) of the derived catalog, cut from the packs' own leaf-cluster atlases. Part of <see cref="PrepareDerivedTreeAssets"/>.
/// Cluster rectangles: the UV triangles of the foliage surfaces (alpha cutout) of LOD0 of every variant of a species are rasterised into a 256 x 256 usage map of the atlas; connected components of at least 0.5 % of the atlas
/// become clusters, their bounding rectangles are shrunk to the alpha bounds at cutoff 0.25; sorted by area then minimum corner (deterministic), the first <see cref="MaxClusters"/> are kept. Meshes (one surface in the source species'
/// foliage material, normalised space: root at the origin, 1 unit tall, so a record's Size is the height in metres): broadleaf dome of 14 cards, pine scrub of 10, fir thicket of 12 in three tiers; LOD1 is three crossed cards and a top card.
/// Vertex normals are bent outward from the shrub's centre (the crown-normal idea of TR-7). Wind attachment (Custom0) = (x, 0, z, 1) per vertex, so the foliage flex of the wind shader is the height fraction squared.
/// </summary>
public partial class PrepareDerivedTreeAssets
{
    public const int UsageSize = 256;
    public const float ClusterCutoff = .25f;
    public const int MaxClusters = 12;
    public const float MinClusterAreaFraction = .005f;
    /// <summary>A cluster rectangle whose alpha coverage is below this is a twig or a stem, not a leaf cluster; one above <see cref="MaxCoverage"/> is an opaque patch (bark), not foliage.</summary>
    public const float MinCoverage = .12f, MaxCoverage = .97f;

    public static string AtlasPath(TreeSpecies species) => species switch
    {
        TreeSpecies.Maple => "res://resources/trees/lolipop/maple/textures/Cluster_Mat_baseColor.png",
        TreeSpecies.Fir => "res://resources/trees/lolipop/fir/textures/Brunches_Mat_baseColor.png",
        _ => "res://resources/trees/lolipop/pine/textures/Clusters_Mat_baseColor.png",
    };

    public readonly record struct ClusterRect(float U0, float V0, float U1, float V1, int UsagePixels, float Coverage)
    {
        public float Width => U1 - U0;
        public float Height => V1 - V0;
        public float Area => Width * Height;
    }

    static bool IsFoliage(Mesh mesh, int surface) => mesh.SurfaceGetMaterial(surface) is ShaderMaterial m && m.GetShaderParameter("use_alpha_cutout").AsBool();

    /// <summary>The cluster rectangles of one species' atlas, unfiltered by quality (see <see cref="ClusterRectangles"/>): area sorted, then by minimum corner.</summary>
    public static List<ClusterRect> AllClusterRectangles(IReadOnlyList<ImportedTreeVariant> catalog, TreeSpecies species, Image atlas)
    {
        var usage = new bool[UsageSize * UsageSize];
        foreach (ImportedTreeVariant variant in catalog.Where(v => v.Species == species).OrderBy(v => v.Index))
        {
            Mesh lod0 = variant.Lods[0];
            for (int s = 0; s < lod0.GetSurfaceCount(); s++)
            {
                if (!IsFoliage(lod0, s)) continue;
                Godot.Collections.Array arrays = lod0.SurfaceGetArrays(s);
                Vector2[] uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                int[] index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, uv.Length).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                for (int t = 0; t + 2 < index.Length; t += 3) Rasterise(usage, uv[index[t]], uv[index[t + 1]], uv[index[t + 2]]);
            }
        }
        Image rgba = (Image)atlas.Duplicate();
        if (rgba.GetFormat() != Image.Format.Rgba8) rgba.Convert(Image.Format.Rgba8);
        byte[] data = rgba.GetData();
        int w = rgba.GetWidth(), h = rgba.GetHeight();
        MaskToLeaves(usage, data, w, h);
        // Connected components (4-neighbour), scanned in raster order so the labelling is deterministic.
        var label = new int[usage.Length];
        var found = new List<(int X0, int Y0, int X1, int Y1, int Count)>();
        var stack = new Stack<int>();
        for (int start = 0; start < usage.Length; start++)
        {
            if (!usage[start] || label[start] != 0) continue;
            int id = found.Count + 1, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            label[start] = id; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % UsageSize, y = p / UsageSize;
                count++; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                if (x > 0 && usage[p - 1] && label[p - 1] == 0) { label[p - 1] = id; stack.Push(p - 1); }
                if (x < UsageSize - 1 && usage[p + 1] && label[p + 1] == 0) { label[p + 1] = id; stack.Push(p + 1); }
                if (y > 0 && usage[p - UsageSize] && label[p - UsageSize] == 0) { label[p - UsageSize] = id; stack.Push(p - UsageSize); }
                if (y < UsageSize - 1 && usage[p + UsageSize] && label[p + UsageSize] == 0) { label[p + UsageSize] = id; stack.Push(p + UsageSize); }
            }
            found.Add((x0, y0, x1, y1, count));
        }
        var rects = new List<ClusterRect>();
        foreach (var (cx0, cy0, cx1, cy1, count) in found)
        {
            if (count < MinClusterAreaFraction * UsageSize * UsageSize) continue;
            int px0 = cx0 * w / UsageSize, px1 = (cx1 + 1) * w / UsageSize, py0 = cy0 * h / UsageSize, py1 = (cy1 + 1) * h / UsageSize;
            int ax0 = int.MaxValue, ay0 = int.MaxValue, ax1 = -1, ay1 = -1, opaque = 0;
            for (int y = py0; y < py1; y++)
                for (int x = px0; x < px1; x++)
                {
                    if (data[(y * w + x) * 4 + 3] / 255f <= ClusterCutoff) continue;
                    opaque++; ax0 = Math.Min(ax0, x); ax1 = Math.Max(ax1, x); ay0 = Math.Min(ay0, y); ay1 = Math.Max(ay1, y);
                }
            if (opaque == 0) continue;
            float coverage = opaque / (float)((ax1 - ax0 + 1) * (ay1 - ay0 + 1));
            rects.Add(new ClusterRect(ax0 / (float)w, ay0 / (float)h, (ax1 + 1) / (float)w, (ay1 + 1) / (float)h, count, coverage));
        }
        return rects.OrderByDescending(r => r.Area).ThenBy(r => r.U0).ThenBy(r => r.V0).ToList();
    }

    /// <summary>The cluster rectangles a look may use: leaf clusters only (alpha coverage between <see cref="MinCoverage"/> and <see cref="MaxCoverage"/>), the largest <see cref="MaxClusters"/>.</summary>
    public static List<ClusterRect> ClusterRectangles(IReadOnlyList<ImportedTreeVariant> catalog, TreeSpecies species, Image atlas)
        => AllClusterRectangles(catalog, species, atlas).Where(r => r.Coverage >= MinCoverage && r.Coverage <= MaxCoverage).Take(MaxClusters).ToList();

    /// <summary>The cards of a tree LOD0 tile their atlas islands edge to edge, so the raw usage map is one blob. Keep the usage where the atlas has leaves: a usage cell stays when some texel within <see cref="GapCells"/> cells of it is opaque at the cutoff (islands closer than that merge).</summary>
    const int GapCells = 2;

    static void MaskToLeaves(bool[] usage, byte[] data, int w, int h)
    {
        var opaque = new bool[usage.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (data[(y * w + x) * 4 + 3] / 255f > ClusterCutoff) opaque[(y * UsageSize / h) * UsageSize + x * UsageSize / w] = true;
        var near = new bool[usage.Length];
        for (int y = 0; y < UsageSize; y++)
            for (int x = 0; x < UsageSize; x++)
            {
                if (!opaque[y * UsageSize + x]) continue;
                for (int dy = -GapCells; dy <= GapCells; dy++)
                    for (int dx = -GapCells; dx <= GapCells; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < UsageSize && ny < UsageSize) near[ny * UsageSize + nx] = true;
                    }
            }
        for (int i = 0; i < usage.Length; i++) usage[i] &= near[i];
    }

    static void Rasterise(bool[] usage, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 pa = a * UsageSize, pb = b * UsageSize, pc = c * UsageSize;
        void Mark(int x, int y) { if (x >= 0 && y >= 0 && x < UsageSize && y < UsageSize) usage[y * UsageSize + x] = true; }
        foreach (Vector2 p in new[] { pa, pb, pc }) Mark((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        int x0 = (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X))), x1 = (int)Math.Floor(Math.Max(pa.X, Math.Max(pb.X, pc.X)));
        int y0 = (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y))), y1 = (int)Math.Floor(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y)));
        float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
        if (Math.Abs(area) < 1e-9f) return;
        for (int y = Math.Max(0, y0); y <= Math.Min(UsageSize - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(UsageSize - 1, x1); x++)
            {
                var p = new Vector2(x + .5f, y + .5f);
                float w0 = ((pb.X - p.X) * (pc.Y - p.Y) - (pc.X - p.X) * (pb.Y - p.Y)) / area, w1 = ((pc.X - p.X) * (pa.Y - p.Y) - (pa.X - p.X) * (pc.Y - p.Y)) / area, w2 = 1f - w0 - w1;
                if (w0 >= 0f && w1 >= 0f && w2 >= 0f) usage[y * UsageSize + x] = true;
            }
    }

    // ---- meshes ------------------------------------------------------------------------------------------------------

    /// <summary>The three looks of A8.</summary>
    public enum ShrubLook { Broadleaf, PineScrub, FirThicket }

    public static ShrubLook LookOf(TreeSpecies species) => species switch { TreeSpecies.Maple => ShrubLook.Broadleaf, TreeSpecies.Pine => ShrubLook.PineScrub, _ => ShrubLook.FirThicket };

    static float Hash01(int form, int k, int salt)
    {
        uint h = ((uint)form * 2654435761u) ^ ((uint)(k + 1) * 40503u * 2246822519u) ^ ((uint)(salt + 1) * 3266489917u);
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
        return (h & 0xFFFFFFu) / 16777216f;
    }

    /// <summary>Quads with outward-bent vertex normals; positions are scaled to a height of 1 by <see cref="Finish"/>.</summary>
    sealed class Cards
    {
        readonly List<Vector3> _p = new(), _ng = new();
        readonly List<float> _t = new();
        readonly List<Vector2> _u = new();
        readonly List<int> _i = new();
        public Vector3 Centre = new(0f, .5f, 0f);
        public int Triangles => _i.Count / 3;

        /// <summary>One card: bottom-edge centre b, horizontal direction right, v direction up (unit), size w x h, the UV rectangle (cropped to the card's aspect, window position from hash01), mirrored when flipU; faces the side of outward.</summary>
        public void Card(Vector3 b, Vector3 right, Vector3 up, float w, float h, ClusterRect r, bool flipU, Vector3 outward, float hash01)
        {
            Vector3 ng = right.Cross(up).Normalized();
            if (ng.Dot(outward) < 0f) { right = -right; ng = -ng; flipU = !flipU; }
            float aspect = w / h, ra = r.Width / r.Height;
            float u0 = r.U0, u1 = r.U1, v0 = r.V0, v1 = r.V1;
            if (ra > aspect * 1.3f) { float keep = aspect * 1.3f / ra, room = (u1 - u0) * (1f - keep); u0 += room * hash01; u1 = u0 + (r.U1 - r.U0) * keep; }
            else if (ra < aspect / 1.3f) { float keep = ra * 1.3f / aspect, room = (v1 - v0) * (1f - keep); v0 += room * hash01; v1 = v0 + (r.V1 - r.V0) * keep; }
            if (flipU) (u0, u1) = (u1, u0);
            Vector3 bl = b - right * (w * .5f), br = b + right * (w * .5f), tl = bl + up * h, tr = br + up * h;
            // Direction of increasing u: right (unflipped) or its reverse; the bitangent sign follows (Godot: BINORMAL = cross(NORMAL, TANGENT) * w must point to the card's top).
            Vector3 tangent = flipU ? -right : right;
            float sign = ng.Cross(tangent).Dot(up) >= 0f ? 1f : -1f;
            int first = _p.Count;
            foreach (var (pos, uv) in new[] { (bl, new Vector2(u0, v1)), (tl, new Vector2(u0, v0)), (tr, new Vector2(u1, v0)), (br, new Vector2(u1, v1)) })
            {
                _p.Add(pos); _ng.Add(ng); _u.Add(uv);
                _t.Add(tangent.X); _t.Add(tangent.Y); _t.Add(tangent.Z); _t.Add(sign);
            }
            // (BL, TL, TR) is clockwise seen from the side ng points to: Godot's front face.
            _i.Add(first); _i.Add(first + 1); _i.Add(first + 2);
            _i.Add(first); _i.Add(first + 2); _i.Add(first + 3);
        }

        /// <summary>Scales to height 1, bends the normals outward from the centre (weight 0.7 radial), writes the wind attachment (x, 0, z, 1) and builds the one-surface mesh.</summary>
        public ArrayMesh Finish(out Vector3 min, out Vector3 max)
        {
            float top = _p.Max(p => p.Y);
            float scale = 1f / top;
            min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity); max = -min;
            var positions = new Vector3[_p.Count]; var normals = new Vector3[_p.Count]; var custom = new float[_p.Count * 4];
            for (int i = 0; i < _p.Count; i++)
            {
                Vector3 p = _p[i] * scale;
                positions[i] = p;
                Vector3 radial = (p - Centre * scale).Normalized();
                normals[i] = _ng[i].Lerp(radial, .7f).Normalized();
                custom[i * 4] = p.X; custom[i * 4 + 1] = 0f; custom[i * 4 + 2] = p.Z; custom[i * 4 + 3] = 1f;
                min = min.Min(p); max = max.Max(p);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Tangent] = _t.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _u.ToArray();
            arrays[(int)Mesh.ArrayType.Custom0] = custom;
            arrays[(int)Mesh.ArrayType.Index] = _i.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
            return mesh;
        }
    }

    static Vector3 Dir(float azimuthRadians) => new(MathF.Cos(azimuthRadians), 0f, MathF.Sin(azimuthRadians));

    /// <summary>One shrub mesh of a look: variant 0 to 3 of the look (the form index seeds every hash), LOD0 or LOD1, from the cluster rectangles of the species (cycled per card, offset by the variant).</summary>
    public static ArrayMesh BuildShrub(ShrubLook look, int formIndex, int variant, int lod, IReadOnlyList<ClusterRect> rects)
    {
        if (rects.Count == 0) throw new InvalidOperationException($"shrub form {formIndex}: the {look} look has no cluster rectangles");
        var cards = new Cards();
        int cardIndex = 0;
        ClusterRect Next() => rects[(cardIndex++ + variant * 2) % rects.Count];
        float H(int k, int salt) => Hash01(formIndex, k, salt);
        float Jit(int k, int salt, float amount) => 1f + amount * (2f * H(k, salt) - 1f);
        Vector3 up = Vector3.Up;
        float azimuthOffset = variant * 0.37f + H(99, 0) * .4f;

        if (lod == 1)
        {
            // LOD1: 3 crossed vertical cards and 1 top card, from the look's largest rectangles.
            float width = look switch { ShrubLook.Broadleaf => 1.1f, ShrubLook.PineScrub => 1.4f, _ => 1.0f };
            float topY = look switch { ShrubLook.Broadleaf => .9f, ShrubLook.PineScrub => .8f, _ => .85f };
            for (int p = 0; p < 3; p++)
            {
                float yaw = Mathf.DegToRad(p * 60f) + azimuthOffset * .25f;
                Vector3 right = Dir(yaw), normal = new(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                cards.Card(Vector3.Zero, right, up, width, 1f, rects[p % rects.Count], p == 1, p % 2 == 0 ? normal : -normal, H(p, 40));
            }
            Vector3 topRight = Dir(azimuthOffset), topUp = Dir(azimuthOffset + Mathf.Pi * .5f);
            cards.Card(new Vector3(0f, topY, 0f) - topUp * .35f, topRight, topUp, .7f, .7f, rects[3 % rects.Count], false, up, H(3, 41));
            return cards.Finish(out _, out _);
        }

        switch (look)
        {
            case ShrubLook.Broadleaf:
            {
                const float W = 1.1f;
                for (int p = 0; p < 3; p++)
                {
                    float yaw = Mathf.DegToRad(p * 60f + variant * 7f) + (H(p, 1) - .5f) * .25f;
                    Vector3 right = Dir(yaw), normal = new(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                    for (int half = 0; half < 2; half++)
                    {
                        int k = p * 2 + half;
                        float sz = Jit(k, 2, .15f);
                        float w = (half == 0 ? W : .75f * W) * sz, h = .55f * Jit(k, 3, .15f);
                        Vector3 offset = right * (W * .15f * (2f * H(k, 4) - 1f)) + normal * (.06f * (half == 0 ? 1f : -1f) + .08f * (H(k, 5) - .5f));
                        float y = half == 0 ? .05f * H(k, 6) : .45f + .05f * (H(k, 6) - .5f);
                        cards.Card(new Vector3(offset.X, y, offset.Z), right, up, w, h, Next(), H(k, 7) > .5f, (half + p) % 2 == 0 ? normal : -normal, H(k, 8));
                    }
                }
                for (int k = 0; k < 6; k++)
                {
                    int id = 10 + k;
                    float az = Mathf.DegToRad(k * 60f + 30f + variant * 13f) + (H(id, 1) - .5f) * .5f;
                    Vector3 dir = Dir(az), tiltUp = (dir * MathF.Sin(Mathf.DegToRad(35f)) + up * MathF.Cos(Mathf.DegToRad(35f))).Normalized();
                    Vector3 right = up.Cross(dir).Normalized();
                    float sz = Jit(id, 2, .15f);
                    Vector3 b = dir * (.18f * Jit(id, 3, .15f)) + up * (.2f + .05f * (k % 2) + .04f * (H(id, 4) - .5f));
                    cards.Card(b, right, tiltUp, .6f * sz, .55f * sz, Next(), H(id, 7) > .5f, dir, H(id, 8));
                }
                for (int k = 0; k < 2; k++)
                {
                    int id = 20 + k;
                    float az = azimuthOffset + k * Mathf.Pi * .6f + H(id, 1) * .6f;
                    Vector3 fwd = Dir(az + Mathf.Pi * .5f), right = Dir(az);
                    Vector3 cup = (fwd * MathF.Cos(Mathf.DegToRad(10f)) + up * MathF.Sin(Mathf.DegToRad(10f))).Normalized();
                    float sz = Jit(id, 2, .15f);
                    Vector3 centre = new Vector3((H(id, 3) - .5f) * .3f, .9f + .06f * H(id, 4), (H(id, 5) - .5f) * .3f);
                    cards.Card(centre - cup * (.35f * sz), right, cup, .7f * sz, .7f * sz, Next(), H(id, 7) > .5f, up, H(id, 8));
                }
                break;
            }
            case ShrubLook.PineScrub:
            {
                cards.Centre = new Vector3(0f, .4f, 0f);
                const float W = 1.4f;
                for (int p = 0; p < 2; p++)
                {
                    float yaw = Mathf.DegToRad(p * 90f + variant * 9f) + (H(p, 1) - .5f) * .3f;
                    Vector3 right = Dir(yaw), normal = new(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                    float sz = Jit(p, 2, .15f);
                    cards.Card(new Vector3(0f, .03f * H(p, 6), 0f) + normal * (.05f * (p == 0 ? 1f : -1f)), right, up, W * sz, .75f * Jit(p, 3, .15f), Next(), H(p, 7) > .5f, p == 0 ? normal : -normal, H(p, 8));
                }
                for (int k = 0; k < 8; k++)
                {
                    int id = 10 + k;
                    float az = Mathf.DegToRad(k * 45f + variant * 11f) + (H(id, 1) - .5f) * .4f;
                    Vector3 dir = Dir(az), tiltUp = (dir * MathF.Sin(Mathf.DegToRad(45f)) + up * MathF.Cos(Mathf.DegToRad(45f))).Normalized();
                    Vector3 right = up.Cross(dir).Normalized();
                    float sz = Jit(id, 2, .15f);
                    Vector3 b = dir * (.12f * Jit(id, 3, .15f)) + up * (.08f + .2f * (k % 2) + .04f * (H(id, 4) - .5f));
                    cards.Card(b, right, tiltUp, .85f * sz, .8f * sz, Next(), H(id, 7) > .5f, dir, H(id, 8));
                }
                break;
            }
            default: // fir thicket: three tiers of four near-horizontal boughs, 15 to 25 degrees down, conical
            {
                cards.Centre = new Vector3(0f, .5f, 0f);
                float[] inner = { .34f, .62f, .90f }, reach = { .6f, .48f, .32f };
                for (int tier = 0; tier < 3; tier++)
                    for (int k = 0; k < 4; k++)
                    {
                        int id = tier * 4 + k;
                        float az = Mathf.DegToRad(k * 90f + tier * 31f + variant * 19f) + (H(id, 1) - .5f) * .5f;
                        Vector3 dir = Dir(az);
                        float down = Mathf.DegToRad(15f + 10f * H(id, 2)), length = reach[tier] * Jit(id, 3, .15f);
                        Vector3 along = (dir * MathF.Cos(down) - up * MathF.Sin(down)).Normalized(); // the bough's long axis: outward and down
                        Vector3 right = up.Cross(dir).Normalized();
                        float width = Math.Max(.4f, 1.15f * length) * Jit(id, 4, .15f);
                        // The card's v direction runs along the bough; its bottom edge is the outer end (the atlas boughs hang with the stem at the top edge: the inner end), so u = across, v from the tip inwards.
                        Vector3 outer = dir * (.03f + length * MathF.Cos(down)) + up * (inner[tier] - length * MathF.Sin(down) + .03f * (H(id, 5) - .5f));
                        cards.Card(outer, right, -along, width, length, Next(), H(id, 7) > .5f, up, H(id, 8));
                    }
                break;
            }
        }
        return cards.Finish(out _, out _);
    }

    /// <summary>The foliage bounds (normalised) of a built shrub mesh and its leaf inclination weights, for the tests and the manifest.</summary>
    public static (Aabb Bounds, int Triangles) Measure(ArrayMesh mesh)
    {
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        return (mesh.GetAabb(), arrays[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3);
    }
}
