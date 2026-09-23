using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Small GDScript-facing bridge used only by catalog GPU regressions.</summary>
public partial class ImportedTreeCatalogDriver : Node
{
    public Mesh? LoadVariantMesh(int index, int lod)
    {
        var variants = ImportedTreeCatalog.Load();
        return index is >= 0 and < ImportedTreeCatalog.VariantCount && lod is >= 0 and < 4 ? variants[index].Lods[lod] : null;
    }

    public Godot.Collections.Array<ShaderMaterial> GetVariantMaterials(int index)
    {
        var result = new Godot.Collections.Array<ShaderMaterial>();
        var variants = ImportedTreeCatalog.Load();
        if (index is < 0 or >= ImportedTreeCatalog.VariantCount) return result;
        foreach (ShaderMaterial material in variants[index].Materials) result.Add(material);
        return result;
    }
}
