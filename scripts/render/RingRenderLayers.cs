using Godot;

namespace RingworldTechDemo.Rendering;

/// <summary>
/// The 3D render-layer bits the game assigns on purpose (VisualInstance3D.layers, Camera3D.cull_mask). Godot layer N is bit N-1.
/// Layer 1 (bit 0) is the default world and layer 2 (bit 1) holds the reactor POI interiors (tools/poi/build_poi_scenes.gd);
/// the bits below are the only others in use. A geometry instance is drawn by a camera when (layers and cull_mask) != 0, so an
/// instance that must disappear for one camera sits ONLY on a dedicated bit that the camera masks out (it is not "added" next to bit 0).
/// The main camera keeps Godot's default cull mask (all 20 bits) and therefore still draws every bit listed here.
/// </summary>
public static class RingRenderLayers
{
    /// <summary>Layer 19. Exposed to the main view, excluded by the precipitation occlusion map camera: grass (cards and standing), trees and impostors, the explorer's own body, weather meshes.</summary>
    public const uint NoPrecipitationOcclusion = 1u << 18;
    /// <summary>Layer 20. Every Light3D lives here so the occlusion map camera (and any other camera that masks it) never culls-in the sun and re-renders its shadow map for that viewport (spike: 337 shadow objects, 213 draws).</summary>
    public const uint Lights = 1u << 19;
    /// <summary>Everything a normal camera draws (Godot's default cull mask: layers 1-20).</summary>
    public const uint AllCameraLayers = 0xFFFFFu;
    /// <summary>Cull mask of the precipitation occlusion map camera: every layer except the two dedicated bits above.</summary>
    public const uint PrecipitationMapCullMask = AllCameraLayers & ~(NoPrecipitationOcclusion | Lights);

    /// <summary>Group that opts any geometry subtree out of the occlusion map (tests, ad hoc fixtures, future systems).</summary>
    public static readonly StringName NoPrecipitationOcclusionGroup = "no_precipitation_occlusion";

    /// <summary>
    /// True when this geometry must not occlude precipitation. Rule (kept in one place, applied on SceneTree.NodeAdded by RingPrecipitationOcclusion):
    /// the node, or any ancestor, is in <see cref="NoPrecipitationOcclusionGroup"/> or is one of the owners below. Owners: standing grass and biome decoration
    /// (grass cards, trees, impostors), the explorer (player body and anything it carries), local weather (bolt, rain haze), distant rain curtains,
    /// cloud layers. Terrain, POIs, construction and water are never excluded.
    /// </summary>
    public static bool IsNoPrecipitationOcclusion(Node node)
    {
        for (Node? n = node; n != null; n = n.GetParent())
        {
            if (n.IsInGroup(NoPrecipitationOcclusionGroup)) return true;
            if (n is RingworldTechDemo.Voxel.LOD.RingStandingGrassLayer or RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer
                or RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController or RingLocalWeatherEffects or RingDistantRainCurtains
                or RingCloudLayer or RingVolumetricCloudLayer) return true;
        }
        return false;
    }

    /// <summary>Applies the layer policy to one node: lights move to <see cref="Lights"/>, excluded geometry to <see cref="NoPrecipitationOcclusion"/>. Returns true when the layers changed.</summary>
    public static bool Apply(Node node)
    {
        if (node is Light3D light)
        {
            if (light.Layers == Lights) return false;
            light.Layers = Lights; return true;
        }
        if (node is GeometryInstance3D geometry && geometry.Layers != 0 && geometry.Layers != NoPrecipitationOcclusion && IsNoPrecipitationOcclusion(node))
        {
            geometry.Layers = NoPrecipitationOcclusion; return true;
        }
        return false;
    }
}
