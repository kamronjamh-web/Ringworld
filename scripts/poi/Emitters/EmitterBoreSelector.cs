using System;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Picks the (at most two) sites whose bore the layer should present: the explorer's nearest site, then the camera's
/// nearest site when it differs. A site qualifies when its horizontal wrapped distance is below
/// <c>range * HoleRadius(kind) / 80</c>, so the main bore (hole 155 m) gets a proportionally larger range. Pure.
/// </summary>
public static class EmitterBoreSelector
{
    public const int MaxSelected = 2;

    /// <summary>Distance limit for a site of this kind, scaled from the regular hole radius.</summary>
    public static double RangeFor(SiteKind kind, double rangeM)
        => rangeM * EmitterSiteApertures.HoleRadius(kind) / EmitterSiteApertures.RegularHoleRadiusM;

    public static int Select(EmitterSitePlacementSource? placements, in RingCanonicalPosition explorer, RingCanonicalPosition? camera,
        double rangeM, Span<int> results)
    {
        if (placements == null || results.Length == 0 || !(rangeM > 0d)) return 0;
        int count = 0;
        if (TryNearestInRange(placements, explorer, rangeM, out int first)) results[count++] = first;
        if (camera is { } view && count < results.Length && TryNearestInRange(placements, view, rangeM, out int second) && (count == 0 || results[0] != second))
            results[count++] = second;
        return count;
    }

    private static bool TryNearestInRange(EmitterSitePlacementSource placements, in RingCanonicalPosition position, double rangeM, out int site)
    {
        site = -1;
        if (!double.IsFinite(position.AroundMeters) || !double.IsFinite(position.AcrossMeters)) return false;
        EmitterSiteRegistry registry = placements.Registry;
        double circumference = placements.CircumferenceMeters;
        double around = position.AroundMeters % circumference;
        if (around < 0d) around += circumference;
        if (!registry.TryGetNearestSite((long)Math.Floor(around), position.AcrossMeters, out int index, out double du, out double dw)) return false;
        double distance = Math.Sqrt(du * du + dw * dw);
        if (distance >= RangeFor(registry[index].Kind, rangeM)) return false;
        site = index;
        return true;
    }
}
