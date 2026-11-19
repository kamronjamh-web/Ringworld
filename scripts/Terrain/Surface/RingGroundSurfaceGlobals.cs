using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Publishes the ground surface shader globals that come from the world (T4 design, "Globals"). Called with the world feature mask, on the main thread, at world load.</summary>
public static class RingGroundSurfaceGlobals
{
    public const string WorldGlobalName = "ringworld_ground_world";

    // The last published value: PublishScatterLook re-sets the whole vec4 and keeps x (strata step) and y (seed) from the last PublishWorld.
    private static Vector4 _published = new((float)RingGroundSurfaceModel.DefaultStrataStepM, 0f, 0f, 0f);

    public static Vector4 WorldValue(TerrainGenerationParameters? recipe)
        => recipe is { IsWorldGenerationRecipe: true }
            ? new Vector4((float)recipe.BreaksStepM, (float)(recipe.Seed & 0xFFFF), 0f, 0f)
            : new Vector4((float)RingGroundSurfaceModel.DefaultStrataStepM, 0f, 0f, 0f);

    /// <summary>The value after <see cref="PublishScatterLook"/>: z = rock density, w = scatter distance scale (T5 design B8/B9).</summary>
    public static Vector4 WithScatterLook(Vector4 world, float density, float distanceScale)
        => new(world.X, world.Y, float.IsFinite(density) ? Mathf.Clamp(density, 0f, 1f) : 0f, float.IsFinite(distanceScale) ? Mathf.Clamp(distanceScale, 0.05f, 4f) : 1f);

    public static void PublishWorld(TerrainGenerationParameters? recipe)
    {
        _published = WorldValue(recipe);
        RenderingServer.GlobalShaderParameterSet(WorldGlobalName, _published);
    }

    /// <summary>
    /// The far rock speckle's look (z density, w distance scale), set by the vegetation settings apply in worlds with scatter.rocks only. Worlds without the bit never call it, so z = w = 0 there
    /// and the global is exactly what <see cref="PublishWorld"/> set. Only writes on a change.
    /// </summary>
    public static void PublishScatterLook(float density, float distanceScale)
    {
        Vector4 value = WithScatterLook(_published, density, distanceScale);
        if (value == _published) return;
        _published = value;
        RenderingServer.GlobalShaderParameterSet(WorldGlobalName, value);
    }

    /// <summary>The last value set through this class (tests).</summary>
    public static Vector4 Published => _published;
}
