using System;
using System.Collections.Generic;
using System.Linq;

namespace RingworldTechDemo.Terrain.Generation;

public enum RecipeFieldKind { Int, Float }

public sealed record RecipeFieldSpec(string Name, string FeatureId, RecipeFieldKind Kind, double Default, double Min, double Max);

public static class WorldRecipeFields
{
    // Append new fields in their permanent canonical order. Frozen feature fields are immutable.
    public static IReadOnlyList<RecipeFieldSpec> Specs { get; } = new RecipeFieldSpec[]
    {
        // water.rivers (open; values provisional until the freeze, docs/t2-river-reset.md)
        new("river_density", "water.rivers", RecipeFieldKind.Float, 1, 0.25, 4),
        new("lake_abundance", "water.rivers", RecipeFieldKind.Float, 0.5, 0, 1),
        new("river_valley_width", "water.rivers", RecipeFieldKind.Float, 1, 0.5, 2),
        new("channel_depth", "water.rivers", RecipeFieldKind.Float, 1, 0.5, 2),
        new("meander_strength", "water.rivers", RecipeFieldKind.Float, 1, 0, 2),
        new("river_ferocity", "water.rivers", RecipeFieldKind.Float, 1, 0.6, 1.5),
    };
}

public sealed class WorldRecipeSchema
{
    public static WorldRecipeSchema Production { get; } = new(WorldGenerationLedger.Production, WorldRecipeFields.Specs);

    public WorldGenerationLedger Ledger { get; }
    public IReadOnlyList<RecipeFieldSpec> Specs { get; }

    public WorldRecipeSchema(WorldGenerationLedger ledger, IEnumerable<RecipeFieldSpec> specs)
    {
        Ledger = ledger;
        Specs = Array.AsReadOnly(specs.ToArray());
    }

    public IReadOnlyList<RecipeFieldSpec> ActiveRecipeFields(int generation, IReadOnlyList<string> review)
    {
        IReadOnlySet<string> features = Ledger.FeatureSet(generation, review);
        return Specs.Where(spec => features.Contains(spec.FeatureId)).ToArray();
    }
}
