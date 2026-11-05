using System;

namespace RingworldTechDemo.Voxel.Phase2;

/// <summary>
/// Single source for the hull's longitudinal structural cue lines (the vertical blue lines on the rim walls). The lines are evenly spaced
/// (circumference / LineCount, no seam gap); the wall-top POI anchors (RimWallTopAnchors) read the same value so a POI cannot drift off its line.
/// </summary>
public static class RingStructuralCues
{
    /// <summary>Cue lines (and wall-top projector sites) per wall around the full circumference.</summary>
    public const int LineCount = 126;

    /// <summary>Default of the hull shader's <c>structural_longitudinal_spacing_meters</c> (Certified circumference / LineCount, 249,332.8 m); keep equal to the shader.</summary>
    public const float ShaderDefaultSpacingMeters = 249332.8f;

    /// <summary>Longitudinal cue spacing for a ring of this circumference (metres, canonical).</summary>
    public static double LongitudinalSpacingMeters(double circumferenceMeters) => circumferenceMeters / LineCount;

    /// <summary>Canonical around of cue line k (0..LineCount-1; other k wrap).</summary>
    public static double CueLineAround(int k, double circumferenceMeters)
    {
        int wrapped = ((k % LineCount) + LineCount) % LineCount;
        return wrapped * LongitudinalSpacingMeters(circumferenceMeters);
    }
}
