using System;
using System.Linq;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Terrain.Generation;

public partial class WorldGenerationBridge : RefCounted
{
    private static WorldGenerationLedger Ledger => WorldGenerationLedger.Production;

    public string LastError { get; private set; } = string.Empty;
    public int Generation => WorldGeneration.Current.Generation;
    public int FeatureMask => WorldGeneration.Current.FeatureMask;
    public int LatestFrozen => Ledger.LatestFrozen;

    public bool PublishRecipeJson(string json)
    {
        TerrainGenerationParameters? recipe = ValidateRecipe(json);
        if (recipe == null) return false;
        if (WorldGeneration.Publish(recipe, recipe.IsWorldGenerationRecipe ? recipe.GeneratorId : "legacy")) return true;
        LastError = "World generation must be published on the main thread.";
        return false;
    }

    public void PublishLegacy(string source) => WorldGeneration.PublishLegacy(source);

    public bool ValidateRecipeJson(string json) => ValidateRecipe(json) != null;

    public int[] ListFrozenGenerations()
        => Ledger.Rows.Select(row => row.Generation).Where(Ledger.IsLoadable).ToArray();

    public string StepForGeneration(int generation)
        => Ledger.IsLoadable(generation) ? Ledger.Rows[generation - 1].Step : string.Empty;

    public string[] ListOpenFeatures()
        => Ledger.Features.Where(feature => feature.FrozenIn == 0).Select(feature => feature.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private TerrainGenerationParameters? ValidateRecipe(string json)
    {
        try
        {
            TerrainGenerationParameters recipe = TerrainGenerationParameters.FromJson(json);
            bool valid = Ledger.Validate(recipe, out string error);
            LastError = error;
            return valid ? recipe : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            LastError = exception.Message;
            return null;
        }
    }
}
