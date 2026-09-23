using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Allocation-free contract checks callable by runtime smoke scenes.</summary>
public static class CaveGenerationContractDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            CaveGenerationContract first = CaveGenerationContract.Create(7331);
            CaveGenerationContract repeated = CaveGenerationContract.Create(7331);
            CaveGenerationContract alternateTerrain = CaveGenerationContract.Create(7332);
            ulong fixture = first.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -42, -7, 0xC0FFEEUL);
            if (fixture != first.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -42, -7, 0xC0FFEEUL)
                || fixture == alternateTerrain.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -42, -7, 0xC0FFEEUL)
                || fixture == first.GetCoordinateSaltKey(-9_223_372_036_854_774_999L, -42, -7, 0xC0FFEEUL)
                || fixture == first.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -41, -7, 0xC0FFEEUL)
                || fixture == first.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -42, -6, 0xC0FFEEUL)
                || fixture == first.GetCoordinateSaltKey(-9_223_372_036_854_775_000L, -42, -7, 0xC0FFEFUL))
                throw new InvalidOperationException("cave coordinate/salt key is not deterministic or distinct");
            if (first.Identity != repeated.Identity || first.Identity == alternateTerrain.Identity
                || first.Version != CurrentVersion)
                throw new InvalidOperationException("cave identity is not deterministic and terrain-derived");
            if (CaveGenerationContract.Create(-1).Identity == first.Identity)
                throw new InvalidOperationException("signed terrain seed did not affect cave identity");
            ThrowsArgument(() => first.ValidateTerrainSeed(7332));
            Throws(() => CaveGenerationContract.Create(
                7331, new CaveGenerationSettings { ContractVersion = CurrentVersion + 1 }));
            summary = $"version={first.Version} terrainSeed={first.TerrainSeed} identity={first.Identity:X16}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private const int CurrentVersion = CaveGenerationContract.CurrentVersion;

    private static void Throws(Action operation)
    {
        try { operation(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("invalid cave generation settings were accepted");
    }

    private static void ThrowsArgument(Action operation)
    {
        try { operation(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("cave contract accepted a different terrain seed");
    }
}
