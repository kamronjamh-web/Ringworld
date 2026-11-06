using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// The 252 atmosphere-shield projector sites (docs/containment-field-design.md 6.1, "Projector POIs status"): one tower on every wall-top anchor
/// (<see cref="RimWallTopAnchors.Build"/>: around = k * C / 126, across = the wall's centre line, vertical = the wall top), in the anchors' order
/// (side, then k). Asset frame: local X = +across (the 80 m body spans the wall; the field plane is X = 0), Y = local up, Z = +around (the 180 m
/// feet run along the wall), origin = the wall-top centre. Pure: no scene access.
/// </summary>
public sealed class AtmosphereProjectorSites
{
    /// <summary>Height of the stream / visibility-range pivot above the wall top: the middle of the 1,600 m tower.</summary>
    public const double MidHeightMeters = 800d;
    public const double TowerHeightMeters = 1600d;

    private readonly RimWallTopAnchor[] _sites;

    public double CircumferenceMeters { get; }
    public double WidthMeters { get; }
    public double WallTopMeters { get; }
    public RimWallDescription Wall { get; }
    public double SpacingMeters => RingStructuralCues.LongitudinalSpacingMeters(CircumferenceMeters);
    public int Count => _sites.Length;
    public int PerWall => RingStructuralCues.LineCount;

    private AtmosphereProjectorSites(IReadOnlyList<RimWallTopAnchor> anchors, double circumference, double width, double wallTop, RimWallDescription wall)
    {
        _sites = new RimWallTopAnchor[anchors.Count];
        for (int i = 0; i < _sites.Length; i++) _sites[i] = anchors[i];
        CircumferenceMeters = circumference;
        WidthMeters = width;
        WallTopMeters = wallTop;
        Wall = wall;
    }

    public static AtmosphereProjectorSites Build(in RingLodGeometrySnapshot snapshot, double widthMeters, in RimWallDescription wall)
        => new(RimWallTopAnchors.Build(snapshot, widthMeters, wall), snapshot.CircumferenceMeters, widthMeters, snapshot.StructuralWallTopGlobalY, wall);

    /// <summary>True when the sites were built for these values (callers rebuild when the grid's circumference, width or wall top change).</summary>
    public bool Matches(double circumference, double width, double wallTop, in RimWallDescription wall)
        => CircumferenceMeters == circumference && WidthMeters == width && WallTopMeters == wallTop && Wall == wall;

    public RimWallTopAnchor this[int index] => _sites[index];

    /// <summary>Site index for wall <paramref name="side"/> (0 = across-0 wall, 1 = across-W wall) and cue line k (wrapped into 0..125).</summary>
    public int IndexOf(int side, int k)
    {
        int n = PerWall;
        return side * n + ((k % n) + n) % n;
    }

    /// <summary>
    /// The site nearest to <paramref name="aroundMeters"/> on wall <paramref name="side"/>, and the canonical point's position in that tower's asset frame
    /// (local X = across - centre line, local Z = around - site around, wrapped to [-C/2, C/2]).
    /// </summary>
    public int NearestOnWall(int side, double aroundMeters, double acrossMeters, out double localX, out double localZ)
    {
        double spacing = SpacingMeters;
        int k = (int)Math.Round(aroundMeters / spacing);
        int index = IndexOf(side, k);
        RimWallTopAnchor site = _sites[index];
        localZ = EmitterPoiStreamerCore.SignedWrap(aroundMeters - site.AroundMeters, CircumferenceMeters);
        localX = acrossMeters - site.AcrossMeters;
        return index;
    }

    /// <summary>Distance from <paramref name="observer"/> to the tower's mid-height point (the streaming metric; the same pivot as the visibility ranges).</summary>
    public double Distance(int index, in RingCanonicalPosition observer)
    {
        RimWallTopAnchor site = _sites[index];
        double du = EmitterPoiStreamerCore.SignedWrap(site.AroundMeters - observer.AroundMeters, CircumferenceMeters);
        double dw = site.AcrossMeters - observer.AcrossMeters;
        double dv = site.VerticalMeters + MidHeightMeters - observer.VerticalMeters;
        return Math.Sqrt(du * du + dw * dw + dv * dv);
    }

    /// <summary>Distance from <paramref name="observer"/> to the tower origin on the wall top.</summary>
    public double GroundDistance(int index, in RingCanonicalPosition observer)
    {
        RimWallTopAnchor site = _sites[index];
        double du = EmitterPoiStreamerCore.SignedWrap(site.AroundMeters - observer.AroundMeters, CircumferenceMeters);
        double dw = site.AcrossMeters - observer.AcrossMeters;
        double dv = site.VerticalMeters - observer.VerticalMeters;
        return Math.Sqrt(du * du + dw * dw + dv * dv);
    }

    /// <summary>Site transform in the current world frame: origin = CanonicalToWorld(anchor) exactly at the wall top; basis X = +across, Y = up, Z = +around.</summary>
    public Transform3D GetWorldTransform(in RingLodGeometrySnapshot snapshot, int index)
        => SiteTransform(new EmitterSiteFrameContext(snapshot), _sites[index]);

    /// <summary>Fills destination[0..Count) with every site's world transform (identical to <see cref="GetWorldTransform"/>). Returns Count.</summary>
    public int FillWorldTransforms(in RingLodGeometrySnapshot snapshot, Span<Transform3D> destination)
    {
        if (destination.Length < _sites.Length) throw new ArgumentException("destination too small", nameof(destination));
        var context = new EmitterSiteFrameContext(snapshot);
        for (int i = 0; i < _sites.Length; i++) destination[i] = SiteTransform(context, _sites[i]);
        return _sites.Length;
    }

    // Yaw 90 degrees about local up turns the surface frame (tangent, inward, -across) into (+across, inward, tangent): cos = 0, sin = 1 exactly.
    private static Transform3D SiteTransform(in EmitterSiteFrameContext context, in RimWallTopAnchor site)
        => context.SiteTransform(site.AroundMeters, site.AcrossMeters, site.VerticalMeters, 0d, 1d);
}
