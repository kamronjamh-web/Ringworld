using System;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// Authoritative dimensions for the ultra-scale procedural Mega Ring. These
/// values are deliberately separate from the certified 10,000 km Halo preset.
/// </summary>
public static class MegaRingScale
{
    public const double RequestedDiameterMeters = 150_000_000_000.0;
    public const double RequestedRadiusMeters = RequestedDiameterMeters * 0.5;
    public const long RingWidthMeters = 1_600_000_000;
    public const long StructuralDepthMeters = 500_000_000;
    public const long ArcChunkMeters = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
    public const long AroundChunkCount = 14_726_215_564;
    public const long AroundVoxelLength = AroundChunkCount * ArcChunkMeters;
    public const double EffectiveCircumferenceMeters = AroundVoxelLength;
    public const double EffectiveRadiusMeters = EffectiveCircumferenceMeters / Math.Tau;

    public static bool Validate(out string error)
    {
        long expectedChunkCount = checked((long)Math.Round(
            Math.Tau * RequestedRadiusMeters / ArcChunkMeters,
            MidpointRounding.AwayFromZero));
        if (expectedChunkCount != AroundChunkCount)
        {
            error = "mega ring chunk count no longer matches its requested radius";
            return false;
        }

        long legacyMaximum = int.MaxValue;
        if (AroundChunkCount <= legacyMaximum || AroundVoxelLength <= legacyMaximum)
        {
            error = "mega ring must exercise 64-bit canonical topology";
            return false;
        }

        if (Math.Abs(EffectiveRadiusMeters - RequestedRadiusMeters) > ArcChunkMeters / Math.Tau)
        {
            error = "integer topology moved the mega ring radius by more than one arc chunk";
            return false;
        }

        error = string.Empty;
        return true;
    }
}