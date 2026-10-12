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
            AssertLayerArray(catalog, "normal_layers");
            AssertLayerArray(catalog, "metallic_roughness_layers");
            Require(catalog.Material.GetShaderParameter("variant_parameters").AsGodotObject() is Texture2D parameters
                && parameters.GetWidth() == ImportedTreeCatalog.VariantCount && parameters.GetHeight() == 3,
                "parameters");
            GD.Print("IMPORTED_TREE_IMPOSTOR_CATALOG_SMOKE=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"IMPORTED_TREE_IMPOSTOR_CATALOG_SMOKE=FAIL: {exception}");
            GetTree().Quit(1);
        }
    }

    private static Image LookupImage(ImportedTreeImpostorCatalog catalog, string parameter) =>
        (catalog.Material.GetShaderParameter(parameter).AsGodotObject() as ImageTexture)?.GetImage()
        ?? throw new InvalidOperationException($"missing {parameter}");

    private static void AssertLookupDimensions(Image image, ImportedTreeImpostorCatalog catalog, string label) =>
        Require(image.GetWidth() == catalog.VertexCount && image.GetHeight() == ImportedTreeCatalog.VariantCount && image.GetFormat() == Image.Format.Rgbaf, $"{label} lookup dimensions");

    private static void AssertLayerArray(ImportedTreeImpostorCatalog catalog, string parameter)
    {
        if (catalog.Material.GetShaderParameter(parameter).AsGodotObject() is not Texture2DArray array)
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
