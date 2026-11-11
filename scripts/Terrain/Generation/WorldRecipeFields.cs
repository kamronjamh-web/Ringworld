using System;
using System.Collections.Generic;
using System.Linq;

namespace RingworldTechDemo.Terrain.Generation;

public enum RecipeFieldKind { Int, Float }

public sealed record RecipeFieldSpec(string Name, string FeatureId, RecipeFieldKind Kind, double Default, double Min, double Max);

public static class WorldRecipeFields
{
    // Append new fields in their permanent canonical order. Frozen feature fields are immutable.
    public static IReadOnlyList<RecipeFieldSpec> Specs { get; } = Array.Empty<RecipeFieldSpec>();
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
