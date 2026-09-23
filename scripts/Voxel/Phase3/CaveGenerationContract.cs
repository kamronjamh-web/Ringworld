using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Stable, platform-independent identity for designed cave generation. Its
/// seed is always the immutable terrain/world seed, while its version remains
/// independent of the terrain generator version.
/// </summary>
public readonly record struct CaveGenerationContract
{
    public const int CurrentVersion = CaveGenerationSettings.CurrentVersion;

    public int TerrainSeed { get; }
    public int Version { get; }
    public ulong Identity { get; }

    private CaveGenerationContract(int terrainSeed, int version, ulong identity)
    {
        TerrainSeed = terrainSeed;
        Version = version;
        Identity = identity;
    }

    public static CaveGenerationContract Create(int terrainSeed, CaveGenerationSettings? settings = null)
    {
        CaveGenerationSettings resolved = settings ?? new CaveGenerationSettings();
        resolved.Validate();
        return new CaveGenerationContract(
            terrainSeed,
            resolved.ContractVersion,
            CalculateIdentity(terrainSeed, resolved.ContractVersion));
    }

    public static ulong CalculateIdentity(int terrainSeed, int version = CurrentVersion)
    {
        if (version != CurrentVersion) throw new ArgumentOutOfRangeException(nameof(version));

        // FNV-1a over explicit little-endian unsigned words. Do not use
        // HashCode: its process-randomized implementation is not a world key.
        ulong hash = 14695981039346656037UL;
        Mix(ref hash, 0x43415645U); // "CAVE"
        Mix(ref hash, (uint)version);
        Mix(ref hash, unchecked((uint)terrainSeed));
        return hash;
    }

    /// <summary>Rejects attempts to reuse this contract for another world seed.</summary>
    public void ValidateTerrainSeed(int terrainSeed)
    {
        if (TerrainSeed != terrainSeed)
            throw new ArgumentException("Cave contract terrain seed does not match the active world terrain seed.", nameof(terrainSeed));
    }

    /// <summary>
    /// Returns a stable cave-topology key for signed world coordinates and a
    /// caller-defined feature salt. This deliberately uses fixed byte mixing
    /// rather than runtime hash implementations, whose results may vary by
    /// process or platform.
    /// </summary>
    public ulong GetCoordinateSaltKey(long around, long vertical, long across, ulong salt)
    {
        ulong hash = Identity;
        Mix(ref hash, unchecked((ulong)around));
        Mix(ref hash, unchecked((ulong)vertical));
        Mix(ref hash, unchecked((ulong)across));
        Mix(ref hash, salt);
        return hash;
    }

    private static void Mix(ref ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }
    }

    private static void Mix(ref ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }
    }
}
