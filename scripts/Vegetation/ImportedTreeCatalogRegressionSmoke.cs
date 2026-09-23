using System;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Headless structural regression for the production imported-tree catalog.</summary>
public partial class ImportedTreeCatalogRegressionSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            var variants = ImportedTreeCatalog.Load();
            Require(variants.Count == ImportedTreeCatalog.VariantCount, "catalog count");
            for (int index = 0; index < variants.Count; index++)
            {
                ImportedTreeVariant variant = variants[index];
                Require(variant.Index == index, $"index {index}");
                Require(index < 12 ? variant.Species == TreeSpecies.Maple : index < 15 ? variant.Species == TreeSpecies.Fir : variant.Species == TreeSpecies.Pine, $"species order {index}");
                Require(variant.Lods.Length == 4 && variant.Materials.Count > 0, $"LOD/material presence {variant.Name}");
                Require(variant.WidthMeters > 0 && variant.HeightMeters > 0 && variant.TrunkRadiusMeters > 0 && variant.TrunkHeightMeters > 0, $"physical metadata {variant.Name}");
                foreach (Mesh mesh in variant.Lods) AssertNormalizedMesh(mesh, variant.Name);
                bool hasMapleBarkUv2 = false;
                foreach (ShaderMaterial material in variant.Materials)
                {
                    Require(material.Shader?.ResourcePath == "res://materials/imported_tree.gdshader", $"production shader {variant.Name}");
                    hasMapleBarkUv2 |= material.GetShaderParameter("use_uv2_for_metallic_roughness").AsBool();
                }
                if (variant.Species == TreeSpecies.Maple) Require(hasMapleBarkUv2, $"maple bark UV2 {variant.Name}");
            }
            GD.Print("IMPORTED_TREE_CATALOG_SMOKE=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"IMPORTED_TREE_CATALOG_SMOKE=FAIL: {exception.Message}");
            GetTree().Quit(1);
        }
    }

    private static void AssertNormalizedMesh(Mesh mesh, string name)
    {
        Require(mesh.GetSurfaceCount() > 0, $"surface count {name}");
        Aabb bounds = mesh.GetAabb();
        Require(bounds.Size.Y > .001f && bounds.Position.Y > -.05f && bounds.Position.Y < .05f, $"root anchoring {name}");
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            Require(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length > 0, $"vertices {name}");
            Require(arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil, $"UV0 {name}");
			if (arrays[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
			{
				float[] tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
				Require(tangents.Length % 4 == 0, $"tangent length {name}");
				for (int i = 0; i < tangents.Length; i += 4) Require(float.IsFinite(tangents[i]) && float.IsFinite(tangents[i + 1]) && float.IsFinite(tangents[i + 2]) && Mathf.Abs(tangents[i + 3]) > .5f, $"tangent validity {name}");
			}
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
