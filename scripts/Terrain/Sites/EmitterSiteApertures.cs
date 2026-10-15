using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// One surface aperture: the circular hole the terrain surface must leave open above a site's beam bore
/// (docs/emitter-poi-streaming-spec.md section A.7). Backend-neutral; Terrain3D consumes it through
/// <c>RingTerrain3DApertureMask</c> and the far terrain backends consume it in M1-8.
/// </summary>
public readonly record struct SurfaceAperture(int SiteIndex, SiteKind Kind, long Around, int Across, float SurfaceHeight, float Radius, float FloorDepth);

/// <summary>
/// Pure functions of (registry, query). Not part of the recipe or any hash: the hole radius is a presentation contract
/// with the POI assets (bore R 75 regular / R 150 main plus a jagged-edge margin), not a terrain-shaping input.
/// </summary>
public static class EmitterSiteApertures
{
    public const float RegularHoleRadiusM = 80f;
    // Measured against MainSpirePOI: ground geometry covers the annulus R 151..158 m fully.
    public const float MainHoleRadiusM = 155f;
    public const float MaxHoleRadiusM = 155f;
    public const float PlugDepthM = 200f;

    // Bore liner (M1-8): generated code geometry just inside the POI bore wall; the plug is open (an iris rim at -200).
    public const float RegularLinerRadiusM = 76f;
    public const float MainLinerRadiusM = 151f;
    public const float LinerTopM = -0.5f;
    public const float DiscardMarginM = 2f;

    public static float HoleRadius(SiteKind kind) => kind == SiteKind.Main ? MainHoleRadiusM : RegularHoleRadiusM;
    public static float LinerRadius(SiteKind kind) => kind == SiteKind.Main ? MainLinerRadiusM : RegularLinerRadiusM;
    /// <summary>Radius inside which the far/ocean terrain shaders discard fragments: the hole plus a 2 m margin.</summary>
    public static float DiscardRadius(SiteKind kind) => HoleRadius(kind) + DiscardMarginM;

    public static SurfaceAperture ApertureFor(in EmitterSite site)
        => new(site.Index, site.Kind, site.Around, site.Across, site.PadHeight, HoleRadius(site.Kind), PlugDepthM);

    /// <summary>
    /// Writes every aperture whose hole disc touches the disc (<paramref name="around"/>, <paramref name="across"/>,
    /// <paramref name="radius"/>) into <paramref name="results"/> (ascending site index) and returns how many exist,
    /// which may exceed the span length.
    /// </summary>
    public static int GetSurfaceApertures(EmitterSiteRegistry? registry, long around, double across, double radius, Span<SurfaceAperture> results)
    {
        if (registry == null || !(radius >= 0d)) return 0;
        Span<int> indices = stackalloc int[32];
        int found = registry.GetSitesWithinRadius(around, across, radius + MaxHoleRadiusM, indices);
        int[]? heap = null;
        if (found > indices.Length)
        {
            heap = new int[found];
            found = registry.GetSitesWithinRadius(around, across, radius + MaxHoleRadiusM, heap);
        }
        ReadOnlySpan<int> list = heap != null ? heap.AsSpan(0, found) : indices.Slice(0, found);
        long circumference = (long)registry.Recipe.CircumferenceM;
        long wrapped = around % circumference;
        if (wrapped < 0) wrapped += circumference;
        int total = 0;
        foreach (int index in list)
        {
            EmitterSite site = registry[index];
            SurfaceAperture aperture = ApertureFor(site);
            long d = wrapped - site.Around;
            long half = circumference >> 1;
            if (d > half) d -= circumference; else if (d < -half) d += circumference;
            double dw = across - site.Across;
            double reach = radius + aperture.Radius;
            if ((double)d * d + dw * dw > reach * reach) continue;
            if (total < results.Length) results[total] = aperture;
            total++;
        }
        return total;
    }

    /// <summary>O(1): the aperture whose hole contains the point, if any (strictly inside the hole radius).</summary>
    public static bool TryGetApertureAt(EmitterSiteRegistry? registry, double around, double across, out SurfaceAperture aperture)
    {
        aperture = default;
        if (registry == null) return false;
        // The influence flag is ignored on purpose: the hole radius is always inside the influence radius, so the
        // nearest-cell site is the only candidate and the hole test is the whole predicate.
        registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
        EmitterSite site = registry[index];
        float radius = HoleRadius(site.Kind);
        if (du * du + dw * dw >= (double)radius * radius) return false;
        aperture = ApertureFor(site);
        return true;
    }

    /// <summary>
    /// O(1): true inside the bore liner column (strictly inside <see cref="LinerRadius"/>); <paramref name="floorHeight"/> is the
    /// height of the open iris rim, <c>PadHeight - PlugDepthM</c>. Since M2-3 the plug is open, so this is NOT walkable ground:
    /// gameplay uses it only as the bore-column predicate and the ground below comes from the underground SDF.
    /// </summary>
    public static bool TryGetBoreFloorAt(EmitterSiteRegistry? registry, double around, double across, out float floorHeight)
    {
        floorHeight = 0f;
        if (registry == null) return false;
        registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
        EmitterSite site = registry[index];
        float radius = LinerRadius(site.Kind);
        if (du * du + dw * dw >= (double)radius * radius) return false;
        floorHeight = site.PadHeight - PlugDepthM;
        return true;
    }
}
