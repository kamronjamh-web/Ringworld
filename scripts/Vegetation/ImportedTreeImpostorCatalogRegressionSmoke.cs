using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Structural wrapper for the shared far-tree asset; it performs no rendering.</summary>
public partial class ImportedTreeImpostorCatalogRegressionSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            ImportedTreeImpostorCatalog catalog = ImportedTreeImpostorCatalog.Load();
            var source = ImportedTreeCatalog.Load();
            Require(catalog.Mesh.GetSurfaceCount() == 1, "one shared surface");
            Require(catalog.VertexCount % 3 == 0, "triangle slots");
            Require(catalog.ExpandedVertexCounts.Count == ImportedTreeCatalog.VariantCount, "variant count");

            Image positions = LookupImage(catalog, "vertex_data");
            AssertLookupDimensions(positions, catalog, "position");
            AssertLookupDimensions(LookupImage(catalog, "normal_data"), catalog, "normal");
            AssertLookupDimensions(LookupImage(catalog, "uv_data"), catalog, "UV");
            AssertLookupDimensions(LookupImage(catalog, "tangent_data"), catalog, "tangent");
            for (int i = 0; i < source.Count; i++)
            {
                Require(source[i].Lods[3].GetSurfaceCount() == 1, $"LOD3 one surface {i}");
                Vector3[] expected = ExpandPositions(source[i].Lods[3]);
                Require(expected.Length == catalog.ExpandedVertexCounts[i], $"expanded count {i}");
                for (int vertex = 0; vertex < expected.Length; vertex++)
                {
                    Require(expected[vertex].IsEqualApprox(catalog.ExpandedPositions[i][vertex]), $"expanded CPU vertex {i}:{vertex}");
                    Color packed = positions.GetPixel(vertex, i);
                    Require(new Vector3(packed.R, packed.G, packed.B).IsEqualApprox(expected[vertex]) && Mathf.IsEqualApprox(packed.A, 1), $"position lookup {i}:{vertex}");
                }
                for (int vertex = expected.Length; vertex < catalog.VertexCount; vertex++)
                {
                    Color padding = positions.GetPixel(vertex, i);
                    Require(new Vector3(padding.R, padding.G, padding.B).IsEqualApprox(Vector3.Zero) && Mathf.IsZeroApprox(padding.A), $"position padding {i}:{vertex}");
                }
            }

            AssertLayerArray(catalog, "albedo_layers");
            AssertLayerArray(catalog, "albedo_layers", catalog.FarQuadMaterial);
            AssertCutoffRows(catalog, ((Texture2D)catalog.Material.GetShaderParameter("variant_parameters").AsGodotObject()).GetImage());
            AssertLayerArray(catalog, "normal_layers");
            AssertLayerArray(catalog, "metallic_roughness_layers");
            Require(catalog.Material.GetShaderParameter("variant_parameters").AsGodotObject() is Texture2D parameters
                && parameters.GetWidth() == ImportedTreeCatalog.VariantCount && parameters.GetHeight() == ImportedTreeImpostorCatalog.ParameterRowCount,
                "parameters");
            AssertFarQuad(catalog, ((Texture2D)catalog.Material.GetShaderParameter("variant_parameters").AsGodotObject()).GetImage());
            GD.Print("IMPORTED_TREE_IMPOSTOR_CATALOG_SMOKE=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"IMPORTED_TREE_IMPOSTOR_CATALOG_SMOKE=FAIL: {exception}");
            GetTree().Quit(1);
        }
    }

    // TR-6: the far quad is one 6-vertex surface on the same shader with far_quad_mode on, and variant_parameters rows 3 and 4 hold the largest LOD3 plane
    // (found here with an independent rounded-key grouping): UV rectangle, width and vertical extent.
    private static void AssertFarQuad(ImportedTreeImpostorCatalog catalog, Image parameters)
    {
        Require(catalog.FarQuadMesh.GetSurfaceCount() == 1 && catalog.FarQuadMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length == ImportedTreeImpostorCatalog.FarQuadVertexCount, "quad mesh has 12 vertices (the camera-facing quad and the horizontal card)");
        Require(catalog.FarQuadTops.Count == ImportedTreeCatalog.VariantCount, "one horizontal card per variant");
        Require(catalog.FarQuadMaterial.Shader == catalog.Material.Shader && catalog.FarQuadMaterial.GetShaderParameter("far_quad_mode").AsBool()
            && !catalog.Material.GetShaderParameter("far_quad_mode").AsBool(), "quad material is the same shader in quad mode, cards are not");
        Require(catalog.FarQuadPlanes.Count == ImportedTreeCatalog.VariantCount, "one plane per variant");
        for (int i = 0; i < ImportedTreeCatalog.VariantCount; i++)
        {
            Vector3[] p = catalog.ExpandedPositions[i]; Vector2[] uv = catalog.ExpandedUvs[i];
            var area = new Dictionary<(int, int, int, int), float>(); var members = new Dictionary<(int, int, int, int), List<int>>();
            var order = new List<(int, int, int, int)>();
            for (int t = 0; t + 2 < p.Length; t += 3)
            {
                Vector3 c = (p[t + 1] - p[t]).Cross(p[t + 2] - p[t]); float a = c.Length() * .5f;
                if (a < 1e-9f) continue;
                Vector3 n = c.Normalized();
                if (n.X < -1e-3f || (Mathf.Abs(n.X) <= 1e-3f && (n.Z < -1e-3f || (Mathf.Abs(n.Z) <= 1e-3f && n.Y < 0)))) n = -n;
                var key = ((int)Mathf.Round(n.X * 200), (int)Mathf.Round(n.Y * 200), (int)Mathf.Round(n.Z * 200), (int)Mathf.Round(n.Dot(p[t]) * 50));
                if (!area.ContainsKey(key)) { area[key] = 0; members[key] = new(); order.Add(key); }
                area[key] += a; members[key].Add(t);
            }
            // Independent card search: flood fill over triangles of one plane that touch (same position and UV at a corner), largest area first.
            List<int> card = new(); float cardArea = -1; var best = order[0];
            bool anyUpright = false; foreach (var key in order) anyUpright |= Mathf.Abs(key.Item2 / 200f) <= .7f;
            foreach (var key in order)
            {
                if (anyUpright && Mathf.Abs(key.Item2 / 200f) > .7f) continue;
                var tris = members[key]; var seen = new bool[tris.Count];
                for (int start = 0; start < tris.Count; start++)
                {
                    if (seen[start]) continue;
                    var component = new List<int> { tris[start] }; seen[start] = true;
                    for (int c = 0; c < component.Count; c++)
                        for (int other = 0; other < tris.Count; other++)
                        {
                            if (seen[other]) continue;
                            bool touches = false;
                            for (int a = 0; a < 3 && !touches; a++) for (int b = 0; b < 3 && !touches; b++)
                                touches = (p[component[c] + a] - p[tris[other] + b]).Length() < 2e-3f && (uv[component[c] + a] - uv[tris[other] + b]).Length() < 2e-3f;
                            if (touches) { seen[other] = true; component.Add(tris[other]); }
                        }
                    float ca = 0; foreach (int t in component) ca += (p[t + 1] - p[t]).Cross(p[t + 2] - p[t]).Length() * .5f;
                    if (ca > cardArea + 1e-6f) { cardArea = ca; card = component; best = key; }
                }
            }
            members[best] = card; area[best] = cardArea;
            Require(Mathf.Abs(cardArea - catalog.FarQuadPlanes[i].Area) <= 0.02f * cardArea, $"card area {i}");
            var normal = new Vector3(best.Item1, best.Item2, best.Item3).Normalized();
            var h = new Vector3(normal.Z, 0, -normal.X); h = h.LengthSquared() > 1e-8f ? h.Normalized() : Vector3.Right;
            float s0 = float.MaxValue, s1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            foreach (int t in members[best]) for (int k = 0; k < 3; k++)
            {
                s0 = Mathf.Min(s0, h.Dot(p[t + k])); s1 = Mathf.Max(s1, h.Dot(p[t + k])); y0 = Mathf.Min(y0, p[t + k].Y); y1 = Mathf.Max(y1, p[t + k].Y);
                uMin = Mathf.Min(uMin, uv[t + k].X); uMax = Mathf.Max(uMax, uv[t + k].X); vMin = Mathf.Min(vMin, uv[t + k].Y); vMax = Mathf.Max(vMax, uv[t + k].Y);
            }
            Color row3 = parameters.GetPixel(i, 3), row4 = parameters.GetPixel(i, 4);
            Require(Mathf.Abs(row4.R - (s1 - s0)) <= 0.03f * (s1 - s0) + 1e-4f && Mathf.Abs(row4.G - y0) <= 0.03f * (y1 - y0) + 1e-4f && Mathf.Abs(row4.B - y1) <= 0.03f * (y1 - y0) + 1e-4f, $"quad bounds {i}");
            // Every vertex of the plane must land on the quad's UV rectangle (u linear in the horizontal coordinate, v in the height).
            foreach (int t in members[best]) for (int k = 0; k < 3; k++)
            {
                float fu = Mathf.Lerp(row3.R, row3.G, (h.Dot(p[t + k]) - s0) / (s1 - s0)), fv = Mathf.Lerp(row3.B, row3.A, (p[t + k].Y - y0) / (y1 - y0));
                Require(Mathf.Abs(fu - uv[t + k].X) < 0.01f && Mathf.Abs(fv - uv[t + k].Y) < 0.01f, $"quad UV rectangle reproduces the plane's UVs {i}: vertex {t + k} uv {uv[t + k]} rect ({fu}, {fv}) row3 {row3}");
            }
            Require(row4.R > 0 && row4.B > row4.G, $"quad size {i}");
            AssertHorizontalCard(catalog, parameters, i);
        }
    }

    // TR-6b: every variant has exactly one horizontal LOD3 card (found here independently: triangles with |normal.y| > .7 joined where they share a vertex position and UV),
    // and rows 5 to 7 describe a quad that reproduces that card's UVs from its XZ positions at the card's height.
    private static void AssertHorizontalCard(ImportedTreeImpostorCatalog catalog, Image parameters, int i)
    {
        Vector3[] p = catalog.ExpandedPositions[i]; Vector2[] uv = catalog.ExpandedUvs[i];
        var horizontal = new List<int>();
        for (int t = 0; t + 2 < p.Length; t += 3)
        {
            Vector3 c = (p[t + 1] - p[t]).Cross(p[t + 2] - p[t]);
            if (c.Length() * .5f >= 1e-9f && Mathf.Abs(c.Normalized().Y) > .7f) horizontal.Add(t);
        }
        var parent = new int[horizontal.Count]; for (int a = 0; a < parent.Length; a++) parent[a] = a;
        int Find(int a) { while (parent[a] != a) a = parent[a]; return a; }
        for (int a = 0; a < horizontal.Count; a++)
            for (int b = a + 1; b < horizontal.Count; b++)
            {
                bool touches = false;
                for (int x = 0; x < 3 && !touches; x++) for (int y = 0; y < 3 && !touches; y++)
                    touches = (p[horizontal[a] + x] - p[horizontal[b] + y]).Length() < 2e-3f && (uv[horizontal[a] + x] - uv[horizontal[b] + y]).Length() < 2e-3f;
                if (touches) parent[Find(a)] = Find(b);
            }
        var roots = new HashSet<int>(); for (int a = 0; a < horizontal.Count; a++) roots.Add(Find(a));
        Require(roots.Count == 1, $"variant {i} has {roots.Count} horizontal LOD3 cards (expected exactly one)");
        ImportedTreeImpostorCatalog.FarQuadTop top = catalog.FarQuadTops[i];
        Require(top.CardCount == 1 && top.FrameCode >= 0, $"horizontal card found by the catalog {i}");
        Color row5 = parameters.GetPixel(i, 5), row6 = parameters.GetPixel(i, 6), row7 = parameters.GetPixel(i, 7);
        Require(row5.B > row5.R && row5.A > row5.G, $"horizontal card UV rectangle {i}");
        // Every vertex of the card must land on the quad's parallelogram at the UVs the mesh gave it.
        var p00 = new Vector2(row6.R, row6.G); var eu = new Vector2(row6.B - row6.R, row6.A - row6.G); var ev = new Vector2(row7.R - row6.R, row7.G - row6.G);
        float det = eu.X * ev.Y - eu.Y * ev.X;
        Require(Mathf.Abs(det) > 1e-9f, $"horizontal card quad is not degenerate {i}");
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (int t in horizontal)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector2 d = new Vector2(p[t + k].X, p[t + k].Z) - p00;
                float a = (d.X * ev.Y - d.Y * ev.X) / det, b = (eu.X * d.Y - eu.Y * d.X) / det;
                float fu = Mathf.Lerp(row5.R, row5.B, a), fv = Mathf.Lerp(row5.G, row5.A, b);
                Require(Mathf.Abs(fu - uv[t + k].X) < 0.01f && Mathf.Abs(fv - uv[t + k].Y) < 0.01f, $"horizontal card quad reproduces the card's UVs {i}: vertex {t + k}");
                yMin = Mathf.Min(yMin, p[t + k].Y); yMax = Mathf.Max(yMax, p[t + k].Y);
            }
        }
        Require(row7.B >= yMin - 1e-4f && row7.B <= yMax + 1e-4f, $"horizontal card height {i}: {row7.B} outside [{yMin}, {yMax}]");
    }

    private static Image LookupImage(ImportedTreeImpostorCatalog catalog, string parameter) =>
        (catalog.Material.GetShaderParameter(parameter).AsGodotObject() as ImageTexture)?.GetImage()
        ?? throw new InvalidOperationException($"missing {parameter}");

    private static void AssertLookupDimensions(Image image, ImportedTreeImpostorCatalog catalog, string label) =>
        Require(image.GetWidth() == catalog.VertexCount && image.GetHeight() == ImportedTreeCatalog.VariantCount && image.GetFormat() == Image.Format.Rgbaf, $"{label} lookup dimensions");

    // TR-7b-2: row 1 alpha is the card cutoff in use, row 2 alpha the far quad's (calibration version 3, or the authored one for both without it); the two tiers share one albedo array while their cutoffs are equal.
    private static void AssertCutoffRows(ImportedTreeImpostorCatalog catalog, Image parameters)
    {
        ImportedTreeImpostorCalibration? calibration = catalog.Calibration;
        Require(calibration == null || (ImportedTreeImpostorCalibration.Version == 3 && calibration.Cutoffs.Length == ImportedTreeCatalog.VariantCount && calibration.QuadCutoffs.Length == ImportedTreeCatalog.VariantCount), "calibration is version 3 with both cutoff sets");
        bool equal = true;
        for (int i = 0; i < ImportedTreeCatalog.VariantCount; i++)
        {
            float card = calibration?.Cutoffs[i] ?? catalog.DefaultCutoffs[i], quad = calibration?.QuadCutoffs[i] ?? catalog.DefaultCutoffs[i];
            Require(Mathf.IsEqualApprox(parameters.GetPixel(i, 1).A, card), $"row 1 alpha is the card cutoff {i}");
            Require(Mathf.IsEqualApprox(parameters.GetPixel(i, 2).A, quad), $"row 2 alpha is the far quad cutoff {i}");
            equal &= card == quad;
        }
        bool shared = ReferenceEquals(catalog.Material.GetShaderParameter("albedo_layers").AsGodotObject(), catalog.FarQuadMaterial.GetShaderParameter("albedo_layers").AsGodotObject());
        Require(shared == equal, "cards and quad share one albedo array exactly when their cutoff sets are equal");
    }

    private static void AssertLayerArray(ImportedTreeImpostorCatalog catalog, string parameter, ShaderMaterial? material = null)
    {
        if ((material ?? catalog.Material).GetShaderParameter(parameter).AsGodotObject() is not Texture2DArray array)
            throw new InvalidOperationException($"missing {parameter}");
        Require(array.GetLayers() == ImportedTreeCatalog.VariantCount, $"{parameter} layer count");
        Require(array.GetWidth() == ImportedTreeImpostorCatalog.TextureLayerSize
            && array.GetHeight() == ImportedTreeImpostorCatalog.TextureLayerSize
            && array.GetFormat() == Image.Format.Rgba8 && array.HasMipmaps(), $"{parameter} array metadata");
        for (int layer = 0; layer < array.GetLayers(); layer++)
        {
            Image? image = array.GetLayerData(layer);
            // The dummy headless renderer retains layered texture metadata but
            // dev7 does not return GPU layer images. Validate that metadata here;
            // real renderer runs still require every layer to read back correctly.
            if (image == null && DisplayServer.GetName() == "headless")
            {
                GD.Print($"IMPORTED_TREE_IMPOSTOR_LAYER_READBACK=SKIP_HEADLESS:{parameter}");
                return;
            }
            if (image == null) throw new InvalidOperationException($"{parameter} layer {layer} readback");
            Require(image.GetWidth() == ImportedTreeImpostorCatalog.TextureLayerSize && image.GetHeight() == ImportedTreeImpostorCatalog.TextureLayerSize && image.HasMipmaps(), $"{parameter} layer {layer}");
        }
    }

    private static Vector3[] ExpandPositions(Mesh mesh)
    {
        var result = new List<Vector3>();
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        if (arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil) result.AddRange(vertices);
        else foreach (int index in arrays[(int)Mesh.ArrayType.Index].AsInt32Array()) result.Add(vertices[index]);
        return result.ToArray();
    }

    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
}
