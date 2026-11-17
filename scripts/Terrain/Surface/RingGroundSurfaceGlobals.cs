using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Publishes the ground surface shader globals that come from the world (T4 design, "Globals"). Called with the world feature mask, on the main thread, at world load.</summary>
public static class RingGroundSurfaceGlobals
{
    public const string WorldGlobalName = "ringworld_ground_world";

    public static Vector4 WorldValue(TerrainGenerationParameters? recipe)
        => recipe is { IsWorldGenerationRecipe: true }
            ? new Vector4((float)recipe.BreaksStepM, (float)(recipe.Seed & 0xFFFF), 0f, 0f)
            : new Vector4((float)RingGroundSurfaceModel.DefaultStrataStepM, 0f, 0f, 0f);

    public static void PublishWorld(TerrainGenerationParameters? recipe)
        => RenderingServer.GlobalShaderParameterSet(WorldGlobalName, WorldValue(recipe));
}
