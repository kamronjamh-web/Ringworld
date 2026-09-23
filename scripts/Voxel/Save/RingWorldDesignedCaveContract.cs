using System;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Save;

/// <summary>
/// Serialized world contract for the designed-cave layer.  It is kept outside
/// TerrainDefinitionHash because that hash predates designed caves; saves use
/// this explicit record instead of silently reinterpreting edited terrain.
/// </summary>
public sealed record RingWorldDesignedCaveContractSnapshot
{
    public const int CurrentVersion = 3;

    public int SnapshotVersion { get; init; } = CurrentVersion;
    public int CaveGeneratorVersion { get; init; }
    public int CaveContractVersion { get; init; }
    /// <summary>The terrain/world seed from which the cave identity is derived.</summary>
    public int TerrainSeed { get; init; }
    public ulong CaveIdentity { get; init; }
    public int CavePlacementVersion { get; init; }
    public ulong CavePlacementIdentity { get; init; }
    public bool DesignedCavesEnabled { get; init; }
    public RingVerticalDomainConfiguration VerticalDomain { get; init; }

    public static RingWorldDesignedCaveContractSnapshot Capture(
        CaveGenerationContract caveContract,
        CaveFeaturePlacementPolicy cavePlacement,
        bool designedCavesEnabled,
        RingVerticalDomainConfiguration verticalDomain)
    {
        if (cavePlacement == null) throw new ArgumentNullException(nameof(cavePlacement));
        if (!verticalDomain.Validate(out string error))
            throw new ArgumentException($"Cannot save an invalid vertical domain: {error}", nameof(verticalDomain));
        return new RingWorldDesignedCaveContractSnapshot
        {
            CaveGeneratorVersion = CaveFeatureFamilyGenerator.Version,
            CaveContractVersion = caveContract.Version,
            TerrainSeed = caveContract.TerrainSeed,
            CaveIdentity = caveContract.Identity,
            CavePlacementVersion = CaveFeaturePlacementPolicy.Version,
            CavePlacementIdentity = cavePlacement.Identity,
            DesignedCavesEnabled = designedCavesEnabled,
            VerticalDomain = verticalDomain
        };
    }
}

/// <summary>Explicit load decisions; none of the rejected states mutate or delete old data.</summary>
public enum RingWorldCaveContractCompatibility
{
    Current,
    LegacyWithoutCaveContract,
    SafelyMigratableAcceptedTerrainHash,
    IncompatibleSaveFormat,
    IncompatibleTerrain,
    IncompatibleContract
}

public readonly record struct RingWorldCaveContractCompatibilityResult(
    RingWorldCaveContractCompatibility Status,
    bool CanLoad,
    bool RequiresRewrite,
    string Reason);

/// <summary>Pure compatibility policy shared by save loading and focused diagnostics.</summary>
public static class RingWorldCaveContractCompatibilityPolicy
{
    private const int FirstSaveFormatWithRequiredCaveContract = 2;

    public static RingWorldCaveContractCompatibilityResult Evaluate(
        int savedFormatVersion,
        int currentSaveFormatVersion,
        int savedTerrainGeneratorVersion,
        int expectedTerrainGeneratorVersion,
        ulong savedTerrainDefinitionHash,
        ulong expectedTerrainDefinitionHash,
        ReadOnlySpan<ulong> acceptedLegacyTerrainHashes,
        double savedCircumferenceMeters,
        double expectedCircumferenceMeters,
        RingWorldDesignedCaveContractSnapshot? savedCaveContract,
        RingWorldDesignedCaveContractSnapshot expectedCaveContract)
    {
        if (expectedCaveContract == null) throw new ArgumentNullException(nameof(expectedCaveContract));
        if (savedFormatVersion < 1 || savedFormatVersion > currentSaveFormatVersion)
            return Reject(RingWorldCaveContractCompatibility.IncompatibleSaveFormat, "Unsupported world save format.");
        if (savedTerrainGeneratorVersion != expectedTerrainGeneratorVersion
            || !double.IsFinite(savedCircumferenceMeters)
            || Math.Abs(savedCircumferenceMeters - expectedCircumferenceMeters) >= 0.01)
            return Reject(RingWorldCaveContractCompatibility.IncompatibleTerrain, "Terrain generator or ring circumference does not match.");

        bool currentTerrainHash = savedTerrainDefinitionHash == expectedTerrainDefinitionHash;
        bool acceptedLegacyHash = Contains(acceptedLegacyTerrainHashes, savedTerrainDefinitionHash);
        if (!currentTerrainHash && !acceptedLegacyHash)
            return Reject(RingWorldCaveContractCompatibility.IncompatibleTerrain, "Terrain definition hash is not accepted for this world.");

        if (savedCaveContract == null)
        {
            if (savedFormatVersion >= FirstSaveFormatWithRequiredCaveContract)
                return Reject(
                    RingWorldCaveContractCompatibility.IncompatibleContract,
                    "Save is missing its required designed-cave contract.");
            // v1 has no way to prove which terrain seed/version generated caves.
            // It is safe only when caves are presently disabled, so loading it
            // cannot change the density underneath an old edit journal.
            if (expectedCaveContract.DesignedCavesEnabled)
                return Reject(RingWorldCaveContractCompatibility.IncompatibleContract,
                    "Legacy save has no designed-cave contract while designed caves are enabled.");
            return new(
                currentTerrainHash
                    ? RingWorldCaveContractCompatibility.LegacyWithoutCaveContract
                    : RingWorldCaveContractCompatibility.SafelyMigratableAcceptedTerrainHash,
                true,
                true,
                currentTerrainHash
                    ? "Legacy v1 save accepted with caves disabled; it will be rewritten with a cave contract."
                    : "Accepted legacy terrain hash migrated with caves disabled; it will be rewritten with a cave contract.");
        }

        if (savedCaveContract.SnapshotVersion != RingWorldDesignedCaveContractSnapshot.CurrentVersion
            || savedCaveContract.CaveGeneratorVersion != expectedCaveContract.CaveGeneratorVersion
            || savedCaveContract.CaveContractVersion != expectedCaveContract.CaveContractVersion
            || savedCaveContract.TerrainSeed != expectedCaveContract.TerrainSeed
            || savedCaveContract.CaveIdentity != expectedCaveContract.CaveIdentity
            || savedCaveContract.CavePlacementVersion != expectedCaveContract.CavePlacementVersion
            || savedCaveContract.CavePlacementIdentity != expectedCaveContract.CavePlacementIdentity
            || savedCaveContract.DesignedCavesEnabled != expectedCaveContract.DesignedCavesEnabled
            || savedCaveContract.VerticalDomain != expectedCaveContract.VerticalDomain
            || !savedCaveContract.VerticalDomain.Validate(out _))
            return Reject(RingWorldCaveContractCompatibility.IncompatibleContract,
                "Designed-cave generator, identity, enabled state, or vertical-domain contract does not match.");

        return new(RingWorldCaveContractCompatibility.Current, true, !currentTerrainHash,
            currentTerrainHash ? "Current designed-cave contract matches." : "Accepted legacy terrain hash will be rewritten.");
    }

    private static RingWorldCaveContractCompatibilityResult Reject(RingWorldCaveContractCompatibility status, string reason) =>
        new(status, false, false, reason);

    private static bool Contains(ReadOnlySpan<ulong> values, ulong candidate)
    {
        foreach (ulong value in values)
            if (value != 0 && value == candidate) return true;
        return false;
    }
}
