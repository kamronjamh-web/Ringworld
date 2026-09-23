using System;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// Versioned ownership contract for the natural-terrain underside, protected
/// roof reserve, cavity, and lower hull. It deliberately stays independent of
/// terrain material settings so topology changes do not mutate sampler hashes.
/// </summary>
public readonly record struct RingVerticalDomainConfiguration(
    int TerrainBottomGlobalY,
    int ProtectedRoofThicknessMeters,
    int StructuralBottomGlobalY,
    int LowerHullThicknessMeters)
{
    public const int CurrentVersion = 1;
    public int ProtectedRoofTopGlobalY => checked(TerrainBottomGlobalY + ProtectedRoofThicknessMeters);
    public int LowerHullTopGlobalY => checked(StructuralBottomGlobalY + LowerHullThicknessMeters);

    public bool Validate(out string error)
    {
        if (ProtectedRoofThicknessMeters <= 0 || LowerHullThicknessMeters <= 0)
        {
            error = "Vertical-domain thicknesses must be positive.";
            return false;
        }

        long protectedRoofTop = (long)TerrainBottomGlobalY + ProtectedRoofThicknessMeters;
        long lowerHullTop = (long)StructuralBottomGlobalY + LowerHullThicknessMeters;
        if (TerrainBottomGlobalY > 0
            || StructuralBottomGlobalY >= TerrainBottomGlobalY
            || protectedRoofTop <= TerrainBottomGlobalY
            || lowerHullTop <= StructuralBottomGlobalY
            || lowerHullTop >= TerrainBottomGlobalY)
        {
            error = "Vertical domains overlap or are inverted.";
            return false;
        }

        const int chunkMeters = VoxelConstants.ChunkSizeY * VoxelConstants.VoxelSizeMeters;
        if (TerrainBottomGlobalY % chunkMeters != 0
            || StructuralBottomGlobalY % chunkMeters != 0
            || ProtectedRoofThicknessMeters % chunkMeters != 0
            || LowerHullThicknessMeters % chunkMeters != 0)
        {
            error = $"Vertical-domain boundaries and thicknesses must align to {chunkMeters} m chunks.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public ulong MixDefinitionHash(ulong sourceHash)
    {
        unchecked
        {
            ulong hash = sourceHash ^ (uint)CurrentVersion;
            hash = (hash * 1099511628211UL) ^ (uint)TerrainBottomGlobalY;
            hash = (hash * 1099511628211UL) ^ (uint)ProtectedRoofThicknessMeters;
            hash = (hash * 1099511628211UL) ^ (uint)StructuralBottomGlobalY;
            return (hash * 1099511628211UL) ^ (uint)LowerHullThicknessMeters;
        }
    }
}
