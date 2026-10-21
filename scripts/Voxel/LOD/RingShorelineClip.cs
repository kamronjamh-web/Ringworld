using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Exact shoreline clipping shared by every terrain tier that draws a water surface: proxy sectors,
/// the global terrain mesh and the scaled far viewport. A triangle with one or two wet vertices is
/// clipped against the zero contour of signed water coverage instead of being kept or dropped whole,
/// so coastlines do not saw-tooth at the lattice resolution.
/// </summary>
internal static class RingShorelineClip
{
    /// <summary>Water slightly overlaps the dry side so independent meshes never leave a crack.</summary>
    public const float OverlapMeters = 0.35f;
    public const int MaxVertices = 5;

    /// <summary>One or two wet vertices: the triangle straddles the shoreline and must be clipped.</summary>
    public static bool IsPartiallyWet(bool aIsWet, bool bIsWet, bool cIsWet)
    {
        int wetCount = (aIsWet ? 1 : 0) + (bIsWet ? 1 : 0) + (cIsWet ? 1 : 0);
        return wetCount is 1 or 2;
    }

    public static bool IsFullyWet(bool aIsWet, bool bIsWet, bool cIsWet) => aIsWet && bIsWet && cIsWet;

    /// <summary>Signed coverage: positive depth of water over terrain when wet, negative height above water when dry.</summary>
    public static float SignedCoverage(bool wet, float waterHeightMinusTerrain)
        => wet
            ? Mathf.Max(0.001f, waterHeightMinusTerrain)
            : -Mathf.Max(0.001f, Mathf.Abs(waterHeightMinusTerrain));

    /// <summary>Parameter along from-&gt;to where the shoreline lies, biased to overlap the dry side slightly.</summary>
    public static float EdgeInterpolation(
        float fromCoverage, float toCoverage, bool fromInside, float edgeLengthMeters, float overlapMeters)
    {
        float denominator = fromCoverage - toCoverage;
        float interpolation = Mathf.Abs(denominator) <= 0.000001f
            ? 0.5f
            : Mathf.Clamp(fromCoverage / denominator, 0.0f, 1.0f);
        float overlapFraction = edgeLengthMeters <= 0.000001f
            ? 0.0f
            : Mathf.Clamp(overlapMeters / edgeLengthMeters, 0.0f, 0.25f);
        return fromInside
            ? Mathf.Min(1.0f, interpolation + overlapFraction)
            : Mathf.Max(0.0f, interpolation - overlapFraction);
    }

    /// <summary>
    /// Clips a triangle against coverage &gt;= 0. Output vertices are barycentric weights over the three
    /// source corners, so callers interpolate whatever attributes they carry. Edge i runs from corner i
    /// to corner (i + 1) % 3. Returns 0 (fully dry) or 3-5 vertices in winding order.
    /// </summary>
    public static int ClipTriangle(
        ReadOnlySpan<float> coverage,
        ReadOnlySpan<float> edgeLengthMeters,
        float overlapMeters,
        Span<Vector3> weights)
    {
        int count = 0;
        int previous = 2;
        bool previousInside = coverage[previous] >= 0.0f;
        for (int current = 0; current < 3; current++)
        {
            bool currentInside = coverage[current] >= 0.0f;
            if (currentInside != previousInside)
            {
                // The edge between corners (previous, current) is edge `previous` for previous == 2 -> 0,
                // otherwise edge `previous`.
                float t = EdgeInterpolation(
                    coverage[previous], coverage[current], previousInside, edgeLengthMeters[previous], overlapMeters);
                Vector3 from = Corner(previous), to = Corner(current);
                weights[count++] = from + (to - from) * t;
            }
            if (currentInside) weights[count++] = Corner(current);
            previous = current;
            previousInside = currentInside;
        }
        return count < 3 ? 0 : count;

        static Vector3 Corner(int index) => index switch
        {
            0 => new Vector3(1.0f, 0.0f, 0.0f),
            1 => new Vector3(0.0f, 1.0f, 0.0f),
            _ => new Vector3(0.0f, 0.0f, 1.0f)
        };
    }
}
