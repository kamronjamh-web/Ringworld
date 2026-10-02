using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Experimental Grass Geometry")]
    /// <summary>Four near blades with two sections each (16 rather than 32 triangles).</summary>
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
        => UseReducedNearGrassGeometry && data.BladesPerSource > 1
            ? _reducedNearGrassClumpMesh ??= RingGrassMeshFactory.CreateClump(2)
            : establishedMesh;

    private Mesh SelectIntermediateGrassGeometry(RingGrassTileData data, Mesh establishedMesh)
        => IsSingleRibbonIntermediateGrassActive
            && data.SourceTier < 2 && data.BladesPerSource > 1
            ? _singleRibbonIntermediateGrassClumpMesh ??= RingGrassMeshFactory.CreateIntermediateClump(true)
            : establishedMesh;
}
