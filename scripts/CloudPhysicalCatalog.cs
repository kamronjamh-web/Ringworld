using System;
using System.Collections.Generic;

namespace RingworldTechDemo;

public enum CloudDensityClass
{
    Thin,
    Medium,
    MediumToDense,
    VeryDense,
    ExtremelyDense
}

public enum CloudOccurrence
{
    Common,
    Uncommon,
    Rare
}

public enum CloudVerticalBehavior
{
    Layered,
    RareVerticalGrowth,
    ModerateVerticalGrowth,
    StrongVerticalDevelopment
}

public readonly record struct CloudPhysicalDefinition(
    string Name,
    string Abbreviation,
    CloudFormationProfile Profile,
    string OverallShape,
    float TypicalViewingMinimumMeters,
    float TypicalViewingMaximumMeters,
    float MinimumAltitudeMeters,
    float MaximumAltitudeMeters,
    float MinimumWidthMeters,
    float MaximumWidthMeters,
    float MinimumThicknessMeters,
    float MaximumThicknessMeters,
    CloudDensityClass Density,
    CloudOccurrence Occurrence,
    CloudVerticalBehavior VerticalBehavior);

/// <summary>
/// Physical-only cloud catalog. Rendering, texture, lighting, and volumetric
/// appearance remain owned by the existing cloud renderers.
/// </summary>
public static class CloudPhysicalCatalog
{
    public static readonly CloudPhysicalDefinition[] All =
    {
        new("Cirrus", "Ci", CloudFormationProfile.Cirrus,
            "Long feathered streaks with tapered hooks and wind-swept filaments.",
            7000, 12000, 6000, 13000, 5000, 100000, 500, 2000,
            CloudDensityClass.Thin, CloudOccurrence.Common, CloudVerticalBehavior.RareVerticalGrowth),
        new("Cirrostratus", "Cs", CloudFormationProfile.Cirrostratus,
            "A broad, nearly featureless transparent veil spanning most of the visible sky.",
            7000, 12000, 6000, 13000, 50000, 500000, 1000, 3000,
            CloudDensityClass.Thin, CloudOccurrence.Uncommon, CloudVerticalBehavior.Layered),
        new("Cirrocumulus", "Cc", CloudFormationProfile.Cirrocumulus,
            "Many small rounded puffs aligned into recognizable ripples or parallel rows.",
            7000, 11000, 6000, 12000, 1000, 20000, 500, 1000,
            CloudDensityClass.Thin, CloudOccurrence.Rare, CloudVerticalBehavior.Layered),
        new("Altostratus", "As", CloudFormationProfile.Altostratus,
            "A broad smooth gray sheet with a level underside and very soft outer boundary.",
            3000, 6500, 2000, 7000, 50000, 500000, 2000, 4000,
            CloudDensityClass.Medium, CloudOccurrence.Uncommon, CloudVerticalBehavior.Layered),
        new("Altocumulus", "Ac", CloudFormationProfile.Altocumulus,
            "Rounded mid-level patches collected into bands and irregular clusters.",
            3000, 6500, 2000, 7000, 2000, 50000, 1000, 3000,
            CloudDensityClass.Medium, CloudOccurrence.Common, CloudVerticalBehavior.RareVerticalGrowth),
        new("Stratus", "St", CloudFormationProfile.Stratus,
            "A low flat blanket with an almost level top and bottom and minimal relief.",
            100, 1600, 0, 2000, 10000, 500000, 200, 1000,
            CloudDensityClass.Medium, CloudOccurrence.Common, CloudVerticalBehavior.Layered),
        new("Stratocumulus", "Sc", CloudFormationProfile.Stratocumulus,
            "Large rolling masses forming a low layer, with broad gaps between connected rolls.",
            700, 1800, 500, 2000, 5000, 100000, 500, 2000,
            CloudDensityClass.MediumToDense, CloudOccurrence.Common, CloudVerticalBehavior.Layered),
        new("Nimbostratus", "Ns", CloudFormationProfile.Nimbostratus,
            "A massive deep overcast sheet with a low ragged base and uninterrupted rain deck.",
            500, 3500, 300, 4000, 100000, 1000000, 3000, 5000,
            CloudDensityClass.VeryDense, CloudOccurrence.Uncommon, CloudVerticalBehavior.Layered),
        new("Cumulus", "Cu", CloudFormationProfile.Cumulus,
            "A flat base supporting separate cotton-like bodies with rounded cauliflower domes.",
            700, 2800, 500, 3000, 500, 5000, 1000, 3000,
            CloudDensityClass.Medium, CloudOccurrence.Common, CloudVerticalBehavior.ModerateVerticalGrowth),
        new("Cumulonimbus", "Cb", CloudFormationProfile.Cumulonimbus,
            "A huge vertical tower with a dark flat base, stacked domes, and a broad spreading anvil.",
            500, 18000, 500, 20000, 5000, 30000, 12000, 18000,
            CloudDensityClass.ExtremelyDense, CloudOccurrence.Rare, CloudVerticalBehavior.StrongVerticalDevelopment)
    };

    private static readonly IReadOnlyDictionary<CloudFormationProfile, CloudPhysicalDefinition> ByProfile =
        BuildProfileMap();

    public static CloudPhysicalDefinition Get(CloudFormationProfile profile)
    {
        if (ByProfile.TryGetValue(profile, out CloudPhysicalDefinition definition))
            return definition;
        return ByProfile[CloudFormationProfile.Cumulus];
    }

    private static IReadOnlyDictionary<CloudFormationProfile, CloudPhysicalDefinition> BuildProfileMap()
    {
        var definitions = new Dictionary<CloudFormationProfile, CloudPhysicalDefinition>();
        foreach (CloudPhysicalDefinition definition in All)
            definitions[definition.Profile] = definition;
        return definitions;
    }

    public static bool Validate(out string summary)
    {
        foreach (CloudPhysicalDefinition cloud in All)
        {
            bool valid = cloud.MinimumAltitudeMeters >= 0.0f
                && cloud.MaximumAltitudeMeters > cloud.MinimumAltitudeMeters
                && cloud.MinimumWidthMeters > 0.0f
                && cloud.MaximumWidthMeters >= cloud.MinimumWidthMeters
                && cloud.MinimumThicknessMeters > 0.0f
                && cloud.MaximumThicknessMeters >= cloud.MinimumThicknessMeters
                && cloud.TypicalViewingMinimumMeters >= cloud.MinimumAltitudeMeters
                && cloud.TypicalViewingMaximumMeters <= cloud.MaximumAltitudeMeters;
            if (!valid)
            {
                summary = $"Invalid physical cloud definition: {cloud.Name} ({cloud.Abbreviation}).";
                return false;
            }
        }
        summary = $"{All.Length} fixed-altitude physical cloud definitions validated.";
        return true;
    }
}
