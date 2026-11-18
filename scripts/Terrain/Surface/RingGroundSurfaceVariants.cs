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

    public static bool WorldHasGroundSurface => WorldGeneration.Has("ground.surface");

    public static string ProxyShaderPath => WorldHasGroundSurface ? ProxyGroundShader : ProxyShader;

    public static string Terrain3DShaderPath => WorldHasGroundSurface ? Terrain3DGroundShader : Terrain3DShader;

    public static string FarShaderPath => WorldHasGroundSurface ? FarGroundShader : FarShader;
}
