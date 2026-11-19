using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// FL-3a (docs/forest-lighting-plan.md section 5): C# side of forest_canopy_shade_gpu_test.gd. Builds the synthetic canopy raster the GDScript fixture uploads as
/// ringworld_canopy_cover: world x = around, world z = across, anchor (0, 0), reference altitude 5 m.
/// Trees (broadleaf porosity 0.2, base 6 m, top 14 m unless noted): a closed stand on a square grid (radius 3.5 m, x 100 to 480, z -60 to 60), one isolated tree of radius 5 m at (0, 200),
/// and a small conifer stand (radius 3 m, base 4 m, top 16 m, porosity 0.3, species factor 0.6) at x 100 to 160, z -300 to -240.
/// </summary>
public partial class ForestCanopyShadeGpuTest : RefCounted
{
    public const float Reference = 5f;
    public int TreeCount { get; private set; }

    public Image BuildRaster(float standSpacing)
    {
        var trees = new List<CanopyTree>();
        ulong id = 1;
        for (float x = 100f; x <= 480f; x += standSpacing)
            for (float z = -60f; z <= 60f; z += standSpacing)
                trees.Add(new CanopyTree(id++, x, z, 3.5f, 6f, 14f, 0.2f, 1f));
        trees.Add(new CanopyTree(id++, 0, 200, 5f, 6f, 14f, 0.2f, 1f));
        for (float x = 100f; x <= 160f; x += standSpacing)
            for (float z = -300f; z <= -240f; z += standSpacing)
                trees.Add(new CanopyTree(id++, x, z, 3f, 4f, 16f, 0.3f, 0.6f));
        TreeCount = trees.Count;
        CanopyRasterResult result = new ForestCanopyRaster().Build(trees.ToArray(), 0, 0, 1_000_000_000.0, Reference);
        return Image.CreateFromData(ForestCanopyRaster.Size, ForestCanopyRaster.Size, true, Image.Format.Rgbah, result.Data);
    }

    // FL-3b: the shipped grass card material (RingGrassCardMaterialConfig defaults, variant D atlas) and card mesh for forest_grass_shade_gpu_test.gd.
    RingGrassCardAssetSet? _grassAssets;

    public ShaderMaterial CreateGrassMaterial()
    {
        _grassAssets = RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD);
        return new RingGrassCardMaterialConfig().CreateCardMaterial(_grassAssets.AtlasTexture);
    }

    public Mesh GrassMesh => _grassAssets!.CardArrayMesh;
}
