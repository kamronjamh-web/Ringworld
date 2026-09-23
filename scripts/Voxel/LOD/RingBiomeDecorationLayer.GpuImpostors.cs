using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    /// <summary>Collapses imported-tree LOD4/5 variants into one custom-data impostor batch per far tile bin.</summary>
    [Export] public bool UseSharedFarTreeImpostors { get; set; } = true;

    private bool EffectiveSharedFarTreeImpostors
        => MergeFarGpuTreeBatches && UseSharedFarTreeImpostors
           && _lod4AuthoredTemplates?.Length == ImportedTreeCatalog.VariantCount;

    private void RegisterSharedFarTreeMaterial(ShaderMaterial material)
    {
        if (_treeMaterials.Contains(material)) return;
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        _treeMaterials.Add(material);
        ConfigureTreeLightingMaterial(material, true, true, true);
    }
}
