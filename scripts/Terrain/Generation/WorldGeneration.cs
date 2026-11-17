using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Terrain.Generation;

public sealed class WorldGenerationState
{
    internal WorldGenerationState(int generation, int featureMask, IReadOnlyList<string> reviewFeatures, string source)
    {
        Generation = generation;
        FeatureMask = featureMask;
        ReviewFeatures = Array.AsReadOnly(reviewFeatures.ToArray());
        Source = source;
    }

    public int Generation { get; }
    public int FeatureMask { get; }
    public IReadOnlyList<string> ReviewFeatures { get; }
    public string Source { get; }
}

/// <summary>The generation published by the launcher before the game enters the tree.</summary>
public static class WorldGeneration
{
    public const string ShaderGlobalName = "ringworld_world_features";

    private static volatile WorldGenerationState _current = new(1, 0, Array.Empty<string>(), "default");

    public static WorldGenerationState Current => _current;

    public static bool Publish(TerrainGenerationParameters? recipe, string source)
    {
        if (recipe is not { IsWorldGenerationRecipe: true }) return PublishLegacy(source);
        WorldGenerationLedger ledger = WorldGenerationLedger.Production;
        if (!ledger.Validate(recipe, out string error))
        {
            GD.PushError(error);
            return false;
        }
        int mask = ledger.MaskFor(recipe.WorldGeneration, recipe.ReviewFeatures);
        if (!PublishState(new WorldGenerationState(recipe.WorldGeneration, mask, recipe.ReviewFeatures, source))) return false;
        RingGroundSurfaceGlobals.PublishWorld(recipe);
        return true;
    }

    public static bool PublishLegacy(string source)
    {
        if (!PublishState(new WorldGenerationState(1, 0, Array.Empty<string>(), source))) return false;
        RingGroundSurfaceGlobals.PublishWorld(null);
        return true;
    }

    public static bool Has(string id) => WorldGenerationLedger.Production.Has(Current.FeatureMask, id);

    internal static bool PublishForTests(int generation, int mask)
        => PublishState(new WorldGenerationState(generation, mask, Array.Empty<string>(), "test"));

    private static bool PublishState(WorldGenerationState state)
    {
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            GD.PushError("World generation must be published on the main thread.");
            return false;
        }
        RenderingServer.GlobalShaderParameterSet(ShaderGlobalName, state.FeatureMask);
        _current = state;
        GD.Print($"WORLD_GENERATION published generation={state.Generation} mask=0x{state.FeatureMask:x} review={string.Join(",", state.ReviewFeatures)} source={state.Source}");
        return true;
    }
}
