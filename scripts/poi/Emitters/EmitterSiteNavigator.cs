using System;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Poi;

public enum EmitterSiteClass { Any, Land, Island, Mountain }

public enum EmitterViewpoint { Entry, Gallery, Far, High }

/// <summary>
/// One teleport destination in canonical metres (around, vertical = feet, across) plus the view direction. The Aim* fields
/// are the point the view is aimed at, for alignment measurements. Flight poses arrive in noclip; the others are grounded.
/// </summary>
public readonly record struct EmitterViewpointPose(
    int Site, EmitterViewpoint Viewpoint, double Around, double Vertical, double Across,
    double YawDeg, double PitchDeg, bool Flight, double AimAround, double AimVertical, double AimAcross, string Detail = "");

/// <summary>
/// Pure site lookup and viewpoint maths for the grave-key teleports and the fresh-world spawn. No scene access: the ground
/// height arrives as a delegate so the land check is unit-testable.
/// </summary>
public sealed class EmitterSiteNavigator
{
    /// <summary>Asset-local EntryApproach marker (x, 0, z) of BeamEmitterPOI.tscn (radius 232) and MainSpirePOI.tscn (radius 930).</summary>
    public const double RegularEntryX = 88.782555, RegularEntryZ = 214.34006, MainEntryX = 355.8956, MainEntryZ = 859.20795;
    /// <summary>Asset-local gallery feet (x, y, 0), from the EmitterReactorTest bookmarks (eye height 1.7 m).</summary>
    public const double RegularGalleryX = 112d, RegularGalleryY = 16d, MainGalleryX = 224d, MainGalleryY = 32d;
    public const double EyeHeightMeters = 1.55, GalleryClearanceMeters = 0.1;
    public const double EntryPitchDegrees = 25d;
    /// <summary>Land check: dry means ground at least this far above sea level and no steeper than the slope limit.</summary>
    public const double DryAboveSeaMeters = 2d, MaxSlopeDegrees = 20d, SlopeSampleMeters = 4d;
    public const double SearchStepMeters = 250d, SearchRadiusMeters = 2000d, HoverAboveMeters = 200d;
    public const int SearchAzimuths = 16;
    public const double DefaultFarKm = 20d, DefaultHighKm = 150d;
    public const double MainFarMultiplier = 2d, HighSideFraction = 0.4;

    private readonly EmitterSitePlacementSource _placements;
    private readonly double _ringRadius, _angleRadius, _seaLevel;

    public EmitterSiteNavigator(EmitterSitePlacementSource placements, double ringRadius, double seaLevel = 0d)
    {
        _placements = placements ?? throw new ArgumentNullException(nameof(placements));
        if (!double.IsFinite(ringRadius) || ringRadius <= 0d) throw new ArgumentOutOfRangeException(nameof(ringRadius));
        _ringRadius = ringRadius;
        _angleRadius = placements.CircumferenceMeters / Math.Tau;
        _seaLevel = seaLevel;
    }

    public int Count => _placements.Count;
    public int MainIndex => _placements.Registry.MainIndex;
    public EmitterSitePlacementSource Placements => _placements;

    public bool TryGetSite(int index, out EmitterSite site)
    {
        if (index < 0 || index >= _placements.Count) { site = default; return false; }
        site = _placements.Registry[index];
        return true;
    }

    /// <summary>Most specific class of a site: Mountain, else Island, else Land.</summary>
    public EmitterSiteClass ClassOf(int index)
    {
        EmitterSite site = _placements.Registry[index];
        return site.IsMountain ? EmitterSiteClass.Mountain : site.IsIsland ? EmitterSiteClass.Island : EmitterSiteClass.Land;
    }

    /// <summary>Land matches every dry site including mountains; Island and Mountain match their own flag.</summary>
    public bool Matches(int index, EmitterSiteClass cls)
    {
        EmitterSite site = _placements.Registry[index];
        return cls switch
        {
            EmitterSiteClass.Any => true,
            EmitterSiteClass.Land => !site.IsIsland,
            EmitterSiteClass.Island => site.IsIsland,
            EmitterSiteClass.Mountain => site.IsMountain,
            _ => false
        };
    }

    /// <summary>Nearest matching site by horizontal distance (around wraps); ties go to the lower index. -1 when none matches.</summary>
    public int FindNearest(double around, double across, EmitterSiteClass cls, bool includeMain = false)
        => FindNearest(around, across, cls, includeMain, out _);

    public int FindNearest(double around, double across, EmitterSiteClass cls, bool includeMain, out double distanceMeters)
    {
        double circumference = _placements.CircumferenceMeters;
        int best = -1;
        double bestSquared = double.MaxValue;
        for (int i = 0; i < _placements.Count; i++)
        {
            if (!includeMain && i == MainIndex) continue;
            if (!Matches(i, cls)) continue;
            EmitterSite site = _placements.Registry[i];
            double du = EmitterPoiStreamerCore.SignedWrap(around - site.Around, circumference), dw = across - site.Across;
            double d2 = du * du + dw * dw;
            if (d2 < bestSquared) { bestSquared = d2; best = i; }
        }
        distanceMeters = best < 0 ? double.PositiveInfinity : Math.Sqrt(bestSquared);
        return best;
    }

    /// <summary>Grid row and column of a regular site; the main site is (MainRow, -1).</summary>
    public (int Row, int Column) GetRowColumn(int index)
    {
        EmitterSiteRegistry registry = _placements.Registry;
        if (index == registry.MainIndex) return (registry.MainRow, -1);
        int columns = registry.Recipe.Columns;
        int packed = index / columns;
        return (packed < registry.MainRow ? packed : packed + 1, index % columns);
    }

    /// <summary>Bearing in degrees [0, 360) of an (around, across) offset, in the same convention as the explorer yaw.</summary>
    public static double BearingDegrees(double deltaAround, double deltaAcross)
    {
        double degrees = Math.Atan2(deltaAcross, deltaAround) * (180d / Math.PI);
        return degrees < 0d ? degrees + 360d : degrees;
    }

    public static double WrapDegrees(double degrees)
    {
        degrees %= 360d;
        if (degrees > 180d) degrees -= 360d; else if (degrees <= -180d) degrees += 360d;
        return degrees;
    }

    /// <summary>
    /// Asset-local (lx, ly, lz) to canonical (around, vertical, across): the site yaw theta rotates the local x/z about local up,
    /// local z points toward -across (matches EmitterSiteFrameContext.SiteTransform).
    /// </summary>
    public (double Around, double Vertical, double Across) AssetLocalToCanonical(int index, double lx, double ly, double lz)
    {
        EmitterSitePlacement p = _placements.GetPlacement(index);
        double cos = Math.Cos(p.YawRadians), sin = Math.Sin(p.YawRadians);
        double fx = cos * lx + sin * lz, fz = -sin * lx + cos * lz;
        return (p.AroundMeters + fx, p.PadHeightMeters + EmitterSiteFrameMath.DefaultPadClearanceMeters + ly, p.AcrossMeters - fz);
    }

    /// <summary>
    /// Exact yaw and pitch (degrees) from an observer to a target on the ring cylinder: (a, v, x) maps to
    /// (cos(phi)(R - v), x, sin(phi)(R - v)); the observer basis is tangent (-sin, 0, cos), up (-cos, 0, -sin), across (0, 1, 0).
    /// Yaw = atan2(across, tangent) like the explorer; positive pitch looks up.
    /// </summary>
    public void LookAt(double fromAround, double fromVertical, double fromAcross, double toAround, double toVertical, double toAcross,
        out double yawDeg, out double pitchDeg)
    {
        double phi0 = fromAround / _angleRadius, phi1 = toAround / _angleRadius;
        double r0 = _ringRadius - fromVertical, r1 = _ringRadius - toVertical;
        double dx = Math.Cos(phi1) * r1 - Math.Cos(phi0) * r0, dz = Math.Sin(phi1) * r1 - Math.Sin(phi0) * r0, dy = toAcross - fromAcross;
        double tangent = -Math.Sin(phi0) * dx + Math.Cos(phi0) * dz;
        double up = -Math.Cos(phi0) * dx - Math.Sin(phi0) * dz;
        yawDeg = Math.Atan2(dy, tangent) * (180d / Math.PI);
        pitchDeg = Math.Atan2(up, Math.Sqrt(tangent * tangent + dy * dy)) * (180d / Math.PI);
    }

    /// <summary>
    /// Dry flat ground check: ground at least 2 m above sea level and the slope from +-4 m samples (both axes) at most 20 degrees.
    /// </summary>
    public static bool IsDryFlat(Func<double, double, float> ground, double seaLevel, double around, double across, double widthMeters, out double height)
    {
        height = double.NaN;
        double margin = SlopeSampleMeters + 2d;
        if (across < margin || across > widthMeters - margin) return false;
        float h = ground(around, across);
        if (!float.IsFinite(h) || h < seaLevel + DryAboveSeaMeters) return false;
        float a0 = ground(around - SlopeSampleMeters, across), a1 = ground(around + SlopeSampleMeters, across);
        float x0 = ground(around, across - SlopeSampleMeters), x1 = ground(around, across + SlopeSampleMeters);
        if (!float.IsFinite(a0) || !float.IsFinite(a1) || !float.IsFinite(x0) || !float.IsFinite(x1)) return false;
        double gradA = (a1 - a0) / (2d * SlopeSampleMeters), gradX = (x1 - x0) / (2d * SlopeSampleMeters);
        double slope = Math.Atan(Math.Sqrt(gradA * gradA + gradX * gradX)) * (180d / Math.PI);
        if (slope > MaxSlopeDegrees) return false;
        height = h;
        return true;
    }

    /// <summary>
    /// The target itself when dry and flat; otherwise the nearest dry flat candidate on rings of 250 m up to 2,000 m with 16 azimuths
    /// (first azimuth wins a tie). False when nothing qualifies.
    /// </summary>
    public static bool TryFindLanding(Func<double, double, float> ground, double seaLevel, double widthMeters, double targetAround, double targetAcross,
        out double around, out double across, out double height, out bool exactTarget)
    {
        exactTarget = true;
        around = targetAround; across = targetAcross;
        if (IsDryFlat(ground, seaLevel, targetAround, targetAcross, widthMeters, out height)) return true;
        exactTarget = false;
        for (double radius = SearchStepMeters; radius <= SearchRadiusMeters + 1e-6; radius += SearchStepMeters)
            for (int k = 0; k < SearchAzimuths; k++)
            {
                double angle = k * (Math.Tau / SearchAzimuths);
                double a = targetAround + radius * Math.Cos(angle), x = targetAcross + radius * Math.Sin(angle);
                if (!IsDryFlat(ground, seaLevel, a, x, widthMeters, out height)) continue;
                around = a; across = x;
                return true;
            }
        height = double.NaN;
        return false;
    }

    /// <summary>Destination for one site and viewpoint. <paramref name="ground"/> (around, across) -> ground height in presentation metres.</summary>
    public EmitterViewpointPose GetPose(int site, EmitterViewpoint viewpoint, Func<double, double, float>? ground, double farKm, double highKm)
    {
        if (site < 0 || site >= _placements.Count) throw new ArgumentOutOfRangeException(nameof(site));
        EmitterSitePlacement p = _placements.GetPlacement(site);
        bool main = p.Kind == SiteKind.Main;
        double aimVertical = p.PadHeightMeters + _placements.GetCrownHeightMeters(site) * 0.5d;
        double side = Math.Cos(p.BearingDegrees * (Math.PI / 180d)) < 0d ? -1d : 1d;
        double clearance = EmitterSiteFrameMath.DefaultPadClearanceMeters;
        switch (viewpoint)
        {
            case EmitterViewpoint.Entry:
            {
                (double a, double v, double x) = AssetLocalToCanonical(site, main ? MainEntryX : RegularEntryX, 0d, main ? MainEntryZ : RegularEntryZ);
                LookAt(a, v + EyeHeightMeters, x, p.AroundMeters, aimVertical, p.AcrossMeters, out double yaw, out _);
                return new EmitterViewpointPose(site, viewpoint, a, p.PadHeightMeters + clearance, x, yaw, EntryPitchDegrees, false,
                    p.AroundMeters, aimVertical, p.AcrossMeters, "entry approach");
            }
            case EmitterViewpoint.Gallery:
            {
                (double a, double v, double x) = AssetLocalToCanonical(site, main ? MainGalleryX : RegularGalleryX, main ? MainGalleryY : RegularGalleryY, 0d);
                v += GalleryClearanceMeters; // feet 0.1 m above the gallery floor
                LookAt(a, v + EyeHeightMeters, x, p.AroundMeters, p.PadHeightMeters, p.AcrossMeters, out double yaw, out double pitch);
                return new EmitterViewpointPose(site, viewpoint, a, v, x, yaw, pitch, true, p.AroundMeters, p.PadHeightMeters, p.AcrossMeters, "gallery");
            }
            case EmitterViewpoint.Far:
                return GetFarPose(site, p, ground, farKm, aimVertical, side, main);
            default:
            {
                double altitude = highKm * 1000d;
                double a = p.AroundMeters + side * HighSideFraction * altitude, v = p.PadHeightMeters + altitude;
                LookAt(a, v, p.AcrossMeters, p.AroundMeters, aimVertical, p.AcrossMeters, out double yaw, out double pitch);
                return new EmitterViewpointPose(site, viewpoint, a, v, p.AcrossMeters, yaw, pitch, true, p.AroundMeters, aimVertical, p.AcrossMeters,
                    $"{highKm:0} km altitude");
            }
        }
    }

    private EmitterViewpointPose GetFarPose(int site, EmitterSitePlacement p, Func<double, double, float>? ground, double farKm, double aimVertical, double side, bool main)
    {
        double distance = farKm * 1000d * (main ? MainFarMultiplier : 1d);
        double targetAround = p.AroundMeters + side * distance, targetAcross = p.AcrossMeters;
        string label = $"{distance / 1000d:0} km";
        if (ground != null && TryFindLanding(ground, _seaLevel, _placements.WidthMeters, targetAround, targetAcross, out double a, out double x, out double h, out bool exact))
        {
            double feet = h + EmitterSiteFrameMath.DefaultPadClearanceMeters;
            LookAt(a, feet + EyeHeightMeters, x, p.AroundMeters, aimVertical, p.AcrossMeters, out double yaw, out double pitch);
            return new EmitterViewpointPose(site, EmitterViewpoint.Far, a, feet, x, yaw, pitch, false, p.AroundMeters, aimVertical, p.AcrossMeters,
                exact ? $"{label} grounded" : $"{label} grounded on nearby dry land ({Math.Sqrt((a - targetAround) * (a - targetAround) + (x - targetAcross) * (x - targetAcross)):0} m off)");
        }
        // Fallback: hover 200 m above the higher of ground and sea at the target.
        float g = ground?.Invoke(targetAround, targetAcross) ?? float.NaN;
        double hover = Math.Max(float.IsFinite(g) ? g : _seaLevel, _seaLevel) + HoverAboveMeters;
        LookAt(targetAround, hover, targetAcross, p.AroundMeters, aimVertical, p.AcrossMeters, out double hoverYaw, out double hoverPitch);
        return new EmitterViewpointPose(site, EmitterViewpoint.Far, targetAround, hover, targetAcross, hoverYaw, hoverPitch, true,
            p.AroundMeters, aimVertical, p.AcrossMeters, $"{label} hovering (no dry land)");
    }

    /// <summary>
    /// First-spawn pose of a fresh runtime-surface world: the main spire's Far viewpoint (land check plus hover fallback).
    /// False when the sampler has no emitter sites (v4, legacy), so the caller keeps its default spawn.
    /// </summary>
    public static bool TryComputeFreshSpawn(RingTerrainSampler? sampler, double ringRadius, double farKm, out EmitterViewpointPose pose)
    {
        pose = default;
        EmitterSiteRegistry? registry = sampler?.EmitterSites;
        if (sampler == null || registry == null) return false;
        var navigator = new EmitterSiteNavigator(new EmitterSitePlacementSource(registry), ringRadius, sampler.SeaLevelY);
        pose = navigator.GetPose(registry.MainIndex, EmitterViewpoint.Far, (a, x) => sampler.GetPresentationSurfaceHeight(a, x), farKm, 0d);
        return true;
    }
}
