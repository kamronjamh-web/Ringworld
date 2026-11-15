using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>GDScript-facing bridge for tree_lod_contact_sheets: the source LOD2 and the three decimated levels of a variant,
/// with the catalog's shared materials on every surface.</summary>
public partial class TreeLodSheetDriver : Node
{
    private readonly System.Collections.Generic.Dictionary<char, PreparedTreeRenderLods> _levels = new();

    public int VariantCount() => ImportedTreeCatalog.Load().Count;
    public string VariantName(int index) => ImportedTreeCatalog.Load()[index].Name;
    public float VariantHeight(int index) => ImportedTreeCatalog.Load()[index].HeightMeters;
    public float VariantWidth(int index) => ImportedTreeCatalog.Load()[index].WidthMeters;
    public string SizeClass(int index) => TreeLodDecimator.ClassFor(index);

    /// <summary>level: "S" source LOD2, or "A", "B", "C" from the level files written by prepare_tree_render_lods.</summary>
    public Mesh? GetMesh(string level, int index)
    {
        var variant = ImportedTreeCatalog.Load()[index];
        if (level == "S") return variant.Lods[2];
        char key = level[0];
        if (!_levels.TryGetValue(key, out PreparedTreeRenderLods? res))
        {
            string path = key == PrepareTreeRenderLods.ShippedLevel ? ImportedTreeRenderLodCatalog.ResourcePath : $"res://.godot/tr4/levels/lod2b_{key}.res";
            res = ResourceLoader.Load<PreparedTreeRenderLods>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (res == null) return null;
            _levels[key] = res;
        }
        if (res.IsSource[index] == 1 || res.Meshes[index] == null) return variant.Lods[2];
        var mesh = (ArrayMesh)res.Meshes[index].Duplicate(false);
        for (int s = 0; s < mesh.GetSurfaceCount(); s++) mesh.SurfaceSetMaterial(s, variant.Lods[2].SurfaceGetMaterial(s));
        return mesh;
    }

    public int Triangles(string level, int index)
    {
        Mesh? mesh = GetMesh(level, index);
        int n = 0;
        for (int s = 0; mesh != null && s < mesh.GetSurfaceCount(); s++) n += ((ArrayMesh)mesh).SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
        return n;
    }

    public Godot.Collections.Array<ShaderMaterial> Materials(int index)
    {
        var result = new Godot.Collections.Array<ShaderMaterial>();
        Mesh lod2 = ImportedTreeCatalog.Load()[index].Lods[2];
        for (int s = 0; s < lod2.GetSurfaceCount(); s++) if (lod2.SurfaceGetMaterial(s) is ShaderMaterial m) result.Add(m);
        return result;
    }
}
