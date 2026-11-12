using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>The frozen world generations and the capabilities available for review.</summary>
public sealed class WorldGenerationLedger
{
    public sealed record FeatureInfo(string Id, int Bit, string Plan, int FrozenIn);
    public sealed record Row(int Generation, string Step, IReadOnlyList<string> Features, string Frozen);

    public static WorldGenerationLedger Production { get; } = new(
        new[] { new Row(1, "baseline", Array.Empty<string>(), "2026-11-10") },
        new[] { new FeatureInfo("water.rivers", 0, "T2", 0) }, 1);

    private readonly Dictionary<string, FeatureInfo> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<FeatureInfo> Features { get; }
    public IReadOnlyList<Row> Rows { get; }
    public int LatestFrozen => Rows[^1].Generation;
    public int RetirementFloor { get; }

    private WorldGenerationLedger(IEnumerable<Row> rows, IEnumerable<FeatureInfo> features, int retirementFloor)
    {
        Rows = Array.AsReadOnly(rows.Select(row => row with { Features = Array.AsReadOnly(row.Features.ToArray()) }).ToArray());
        Features = Array.AsReadOnly(features.ToArray());
        if (Rows.Count == 0 || Rows.Where((row, index) => row.Generation != index + 1).Any())
            throw new ArgumentException("Generation rows must start at 1 and be contiguous.", nameof(rows));
        if (retirementFloor < 1 || retirementFloor > LatestFrozen)
            throw new ArgumentOutOfRangeException(nameof(retirementFloor));
        RetirementFloor = retirementFloor;

        var bits = new HashSet<int>();
        foreach (FeatureInfo feature in Features)
        {
            if (!IsFeatureId(feature.Id) || !_byId.TryAdd(feature.Id, feature))
                throw new ArgumentException($"Invalid or duplicate world feature ID '{feature.Id}'.", nameof(features));
            if (feature.Bit is < 0 or > 30 || !bits.Add(feature.Bit))
                throw new ArgumentException($"Invalid or duplicate bit {feature.Bit} for world feature '{feature.Id}'.", nameof(features));
            if (feature.FrozenIn < 0 || feature.FrozenIn > LatestFrozen)
                throw new ArgumentException($"Invalid freeze generation for world feature '{feature.Id}'.", nameof(features));
        }

        var frozen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Row row in Rows)
            foreach (string id in row.Features)
                if (!_byId.TryGetValue(id, out FeatureInfo? feature) || feature.FrozenIn != row.Generation || !frozen.Add(id))
                    throw new ArgumentException($"Invalid feature '{id}' in generation {row.Generation}.", nameof(rows));
        if (Features.Any(feature => feature.FrozenIn > 0 && !frozen.Contains(feature.Id)))
            throw new ArgumentException("A frozen feature is missing from its generation row.", nameof(rows));
    }

    public static WorldGenerationLedger CreateForTests(IEnumerable<Row> rows, IEnumerable<FeatureInfo> features, int retirementFloor = 1)
        => new(rows, features, retirementFloor);

    public bool IsLoadable(int generation)
        => generation == 1 || (generation >= RetirementFloor && generation <= LatestFrozen);

    public IReadOnlySet<string> FeatureSet(int generation, IReadOnlyList<string> review)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (FeatureInfo feature in Features)
            if (feature.FrozenIn > 0 && feature.FrozenIn <= generation)
                active.Add(feature.Id);
        foreach (string id in review)
            if (_byId.ContainsKey(id))
                active.Add(id);
        return active;
    }

    public int MaskFor(int generation, IReadOnlyList<string> review)
    {
        int mask = 0;
        foreach (string id in FeatureSet(generation, review))
            mask |= 1 << _byId[id].Bit;
        return mask;
    }

    public bool Has(int mask, string id) => _byId.TryGetValue(id, out FeatureInfo? feature) && (mask & (1 << feature.Bit)) != 0;

    public bool Validate(TerrainGenerationParameters parameters, out string error)
        => Validate(parameters.WorldGeneration, parameters.ReviewFeatures, out error);

    internal bool Validate(int generation, IReadOnlyList<string> review, out string error)
    {
        if (!IsLoadable(generation))
        {
            error = $"World generation {generation} is unavailable.";
            return false;
        }
        foreach (string id in review)
        {
            if (!_byId.TryGetValue(id, out FeatureInfo? feature))
            {
                error = $"Unknown review feature '{id}'.";
                return false;
            }
            if (feature.FrozenIn > 0 && feature.FrozenIn <= generation)
            {
                error = $"Review feature '{id}' is already part of world generation {generation}.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    internal static string[] ParseReviewFeatures(string csv)
    {
        if (csv.Length == 0) return Array.Empty<string>();
        string[] ids = csv.Split(',');
        for (int i = 0; i < ids.Length; i++)
            if (!IsFeatureId(ids[i]) || (i > 0 && string.CompareOrdinal(ids[i - 1], ids[i]) >= 0))
                throw new ArgumentException("review_features must contain sorted, unique feature IDs.");
        return ids;
    }

    internal static bool IsFeatureId(string id) => Regex.IsMatch(id, @"\A[a-z][a-z0-9_.]*\z");
}
