using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Experimental Grass Geometry")]
    /// <summary>Uses 16 near triangles: eight one-section meadow blades or four two-section legacy blades.</summary>
    [Export] public bool UseReducedNearGrassGeometry { get; set; }
    /// <summary>One intermediate ribbon per source; requires full camera facing before the LOD handoff.</summary>
    [Export] public bool UseSingleRibbonIntermediateGrass { get; set; }

    private Mesh? _reducedNearGrassClumpMesh;
    private Mesh? _singleRibbonIntermediateGrassClumpMesh;

    internal bool IsSingleRibbonIntermediateGrassActive => UseSingleRibbonIntermediateGrass
        && EnableDistanceLod && EnableGrassCameraFacing && GrassCameraFacingStrength >= 1f
        && float.IsFinite(GrassCameraFacingStrength)
        && float.IsFinite(GrassCameraFacingStartMeters) && float.IsFinite(GrassCameraFacingEndMeters)
        && float.IsFinite(LodNearDistanceMeters) && float.IsFinite(LodTransitionWidthMeters)
        && Mathf.Max(Mathf.Clamp(GrassCameraFacingEndMeters, 1f, 2000f),
            Mathf.Clamp(GrassCameraFacingStartMeters, 0f, 1500f) + .01f)
            <= Mathf.Max(1f, LodNearDistanceMeters) - Mathf.Max(1f, LodTransitionWidthMeters) * .5f;

    private Mesh SelectNearGrassGeometry(RingGrassTileData data, Mesh establishedMesh)
        => EnableMeadowTufts && data.BladesPerSource == 8
            ? MeadowMeshFor(data, UseReducedNearGrassGeometry ? "near_reduced" : "near")
            : UseReducedNearGrassGeometry && data.BladesPerSource > 1
            ? _reducedNearGrassClumpMesh ??= RingGrassMeshFactory.CreateClump(2)
            : establishedMesh;

    private Mesh SelectIntermediateGrassGeometry(RingGrassTileData data, Mesh establishedMesh)
        => EnableMeadowTufts && data.BladesPerSource == 8
            ? MeadowMeshFor(data, data.SourceTier >= 2 ? "far" : "intermediate")
            : IsSingleRibbonIntermediateGrassActive
            && data.SourceTier < 2 && data.BladesPerSource > 1
            ? _singleRibbonIntermediateGrassClumpMesh ??= RingGrassMeshFactory.CreateIntermediateClump(true)
            : establishedMesh;
    private Mesh MeadowMeshFor(RingGrassTileData data, string tier)
    {
        int variant = System.Math.Clamp(MeadowTuftVariant, 0, 3) - 1;
        if (variant < 0)
        {
            ulong hash = unchecked((ulong)data.Key.Around * 0x9E3779B185EBCA87UL ^ (ulong)data.Key.Across * 0xC2B2AE3D27D4EB4FUL);
            hash ^= hash >> 29;
            variant = (int)(hash % 3);
        }
        return _meadowMeshes.Get(variant, tier, EffectiveMeadowSpread);
    }
}
