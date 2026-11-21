using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Compile-time selection of the T4 ground surface (T4 design D7): worlds with ground.surface use the *_ground.gdshader variants of the near terrain shaders (full copies with RING_GROUND_SURFACE defined);
/// every other world keeps the shipped shaders, which are the frozen generation 1 code. Choose when a material is created (main thread,
/// after WorldGeneration.Publish at world load); a material never changes variant afterwards.
/// </summary>
public static class RingGroundSurfaceVariants
{
    public const string ProxyShader = "res://materials/ring_terrain_proxy.gdshader";
    public const string ProxyGroundShader = "res://materials/ring_terrain_proxy_ground.gdshader";
    public const string Terrain3DShader = "res://materials/terrain3d_ring_surface.gdshader";
    public const string Terrain3DGroundShader = "res://materials/terrain3d_ring_surface_ground.gdshader";
    public const string FarShader = "res://materials/ring_terrain_global.gdshader";
    public const string FarGroundShader = "res://materials/ring_terrain_global_ground.gdshader";

    /// <summary>T8-R2: the rock face of ground.surface worlds (ambientCG Rock051, CC0; see the folder's LICENSE.md). The albedo is bound as ground_rock_albedo; the normal is the project global ringworld_ground_rock_detail_normal.</summary>
    public const string RockFaceAlbedoPath = "res://materials/ground/rock_face/rock_face_albedo.png";
    public const string RockFaceNormalPath = "res://materials/ground/rock_face/rock_face_normal.png";

    public static bool WorldHasGroundSurface => WorldGeneration.Has("ground.surface");

    public static string ProxyShaderPath => WorldHasGroundSurface ? ProxyGroundShader : ProxyShader;

    public static string Terrain3DShaderPath => WorldHasGroundSurface ? Terrain3DGroundShader : Terrain3DShader;

    public static string FarShaderPath => WorldHasGroundSurface ? FarGroundShader : FarShader;
}
