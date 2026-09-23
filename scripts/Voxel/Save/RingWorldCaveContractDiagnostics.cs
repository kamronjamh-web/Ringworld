using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Save;

/// <summary>Filesystem-free contract migration coverage for runtime smoke tests.</summary>
public static class RingWorldCaveContractDiagnostics
{
    public static bool RunSelfTest(out string report)
    {
        try
        {
            RingVerticalDomainConfiguration domain = new(-8192, 512, -22304, 800);
            CaveGenerationContract caves = CaveGenerationContract.Create(7331);
            var placement = new CaveFeaturePlacementPolicy(
                50_000,
                domain.TerrainBottomGlobalY,
                domain.ProtectedRoofTopGlobalY,
                domain.LowerHullTopGlobalY,
                domain.StructuralBottomGlobalY,
                surfaceFloorMeters: -255);
            RingWorldDesignedCaveContractSnapshot disabled = RingWorldDesignedCaveContractSnapshot.Capture(caves, placement, false, domain);
            RingWorldDesignedCaveContractSnapshot enabled = RingWorldDesignedCaveContractSnapshot.Capture(caves, placement, true, domain);
            const ulong terrain = 0x1234UL;
            const ulong legacyTerrain = 0x5678UL;

            Assert(Evaluate(3, terrain, disabled, disabled).Status == RingWorldCaveContractCompatibility.Current, "current contract rejected");
            Assert(Evaluate(1, terrain, null, disabled).Status == RingWorldCaveContractCompatibility.LegacyWithoutCaveContract, "v1 disabled cave migration rejected");
            Assert(Evaluate(1, legacyTerrain, null, disabled).Status == RingWorldCaveContractCompatibility.SafelyMigratableAcceptedTerrainHash, "accepted legacy terrain hash rejected");
            Assert(!Evaluate(1, terrain, null, enabled).CanLoad, "v1 cave-enabled save was accepted without a contract");
            Assert(!Evaluate(2, terrain, null, disabled).CanLoad, "v2 save without its required cave contract was accepted");
            Assert(!Evaluate(3, terrain, null, disabled).CanLoad, "v3 save without its required cave contract was accepted");
            Assert(!Evaluate(3, terrain, Clone(enabled, identity: enabled.CaveIdentity + 1), enabled).CanLoad, "changed cave identity accepted");
            Assert(!Evaluate(3, terrain, Clone(enabled, placementIdentity: enabled.CavePlacementIdentity + 1), enabled).CanLoad, "changed cave placement accepted");
            Assert(!Evaluate(3, terrain, Clone(enabled, domain: new RingVerticalDomainConfiguration(-8192, 512, -22560, 800)), enabled).CanLoad, "changed vertical domain accepted");
            Assert(!Evaluate(3, 0x9abcUL, enabled, enabled).CanLoad, "unknown terrain hash accepted");
            report = "current, v1 no-contract, v2/v3 required-contract rejection, accepted legacy hash, cave/placement mismatch, vertical-domain mismatch, and unknown terrain rejection passed";
            return true;
        }
        catch (System.Exception exception)
        {
            report = exception.Message;
            return false;
        }
    }

    private static RingWorldCaveContractCompatibilityResult Evaluate(int format, ulong terrain, RingWorldDesignedCaveContractSnapshot? saved, RingWorldDesignedCaveContractSnapshot expected) =>
        RingWorldCaveContractCompatibilityPolicy.Evaluate(format, 3, 7, 7, terrain, 0x1234UL, new ulong[] { 0x5678UL }, 1000, 1000, saved, expected);

    private static RingWorldDesignedCaveContractSnapshot Clone(RingWorldDesignedCaveContractSnapshot source, ulong? identity = null, ulong? placementIdentity = null, RingVerticalDomainConfiguration? domain = null) =>
        new()
        {
            SnapshotVersion = source.SnapshotVersion,
            CaveGeneratorVersion = source.CaveGeneratorVersion,
            CaveContractVersion = source.CaveContractVersion,
            TerrainSeed = source.TerrainSeed,
            CaveIdentity = identity ?? source.CaveIdentity,
            CavePlacementVersion = source.CavePlacementVersion,
            CavePlacementIdentity = placementIdentity ?? source.CavePlacementIdentity,
            DesignedCavesEnabled = source.DesignedCavesEnabled,
            VerticalDomain = domain ?? source.VerticalDomain
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new System.InvalidOperationException(message);
    }
}
