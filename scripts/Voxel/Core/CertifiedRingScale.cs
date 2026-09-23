using System;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// Single source of truth for the production certification dimensions. Runtime
/// topology uses an integer number of 32 m chunks, so its circumference wraps
/// exactly even though the requested radius cannot map to a perfect integer
/// chunk count.
/// </summary>
public static class CertifiedRingScale
{
    public static readonly double RequestedRadiusMeters = 5_000_000.0;
    public static readonly int RingWidthMeters = 318_000;
    public static readonly int TerrainFloorMeters = -8_192;
    public static readonly int ProtectedRoofThicknessMeters = 512;
    public static readonly int LowerHullThicknessMeters = 800;
    public static readonly int TerrainCeilingMeters = 8_000;
    public static readonly int SeaLevelMeters = 0;
    public static readonly int RoutineMountainCeilingMeters = 5_200;
    public static readonly int ExceptionalSummitMeters = 7_600;
    public static readonly int TerrainToBuildClearanceMeters = 400;
    public static readonly int StructuralWallClearanceMeters = 2_000;
    public static readonly int StructuralWallTopMeters = TerrainCeilingMeters + StructuralWallClearanceMeters;
    public static readonly double StructuralDepthMeters = 22_300.0;
    public const int ArcChunkMeters = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
    public const int AroundChunkCount = 981_748;
    public static readonly int AroundVoxelLength = AroundChunkCount * VoxelConstants.ChunkSizeX;
    public const double EffectiveCircumferenceMeters = AroundChunkCount * (double)ArcChunkMeters;
    public static readonly double EffectiveRadiusMeters = EffectiveCircumferenceMeters / Math.Tau;

    public static int CalculateAroundChunkCount(double requestedRadiusMeters)
    {
        return Math.Max(1, checked((int)Math.Round(
            Math.Tau * requestedRadiusMeters / ArcChunkMeters,
            MidpointRounding.AwayFromZero)));
    }

    public static bool Validate(out string error)
    {
        if (CalculateAroundChunkCount(RequestedRadiusMeters) != AroundChunkCount)
        {
            error = "certified chunk count no longer matches the requested radius";
            return false;
        }

        if (AroundVoxelLength <= 0 || AroundVoxelLength >= int.MaxValue)
        {
            error = "certified circumference does not fit the canonical voxel topology";
            return false;
        }

        if (Math.Abs(EffectiveRadiusMeters - RequestedRadiusMeters) > ArcChunkMeters / Math.Tau)
        {
            error = "integer topology moved the effective radius by more than one arc chunk";
            return false;
        }

        if (TerrainFloorMeters >= TerrainCeilingMeters
            || SeaLevelMeters <= TerrainFloorMeters
            || SeaLevelMeters >= TerrainCeilingMeters
            || RoutineMountainCeilingMeters >= ExceptionalSummitMeters
            || ExceptionalSummitMeters > TerrainCeilingMeters - TerrainToBuildClearanceMeters
            || StructuralWallClearanceMeters <= 0
            || StructuralWallTopMeters <= TerrainCeilingMeters
            || StructuralDepthMeters <= LowerHullThicknessMeters
            || TerrainFloorMeters + ProtectedRoofThicknessMeters >= SeaLevelMeters
            || TerrainFloorMeters % ArcChunkMeters != 0
            || ProtectedRoofThicknessMeters % ArcChunkMeters != 0
            || LowerHullThicknessMeters % ArcChunkMeters != 0)
        {
            error = "certified vertical dimensions are invalid";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
