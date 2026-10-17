using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Double-precision 3-vector used for frame and beam math before the final float narrowing.</summary>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public double Dot(Vector3D o) => X * o.X + Y * o.Y + Z * o.Z;
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vector3D Normalized() { double l = Length; return new(X / l, Y / l, Z / l); }
    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>
/// Geometry of one future eclipse beam (user decision U2: converges exactly on the ring hub).
/// <see cref="OriginWorld"/> is the bore centre at crown height; <see cref="Direction"/> is the unit vector to the hub.
/// </summary>
public readonly record struct EmitterBeamGeometry(
    Vector3 OriginWorld,
    Vector3D Direction,
    Vector3D Up,
    double TiltDegrees,
    double DistanceToHubMeters);

/// <summary>Marker offsets read from BeamEmitterPOI.tscn (asset-local metres).</summary>
public readonly record struct EmitterPoiMarkers(Vector3 BeamOrigin, Vector3 BeamCrownFocus, Vector3 ShaftBottom, Vector3 EntryApproach)
{
    /// <summary>Parses the Marker3D translations out of the scene text (no scene load needed; usable from any thread).</summary>
    public static bool TryParse(string sceneText, out EmitterPoiMarkers markers)
    {
        markers = default;
        if (!TryFind(sceneText, "BeamOrigin", out Vector3 origin) || !TryFind(sceneText, "BeamCrownFocus", out Vector3 crown)
            || !TryFind(sceneText, "ShaftBottom", out Vector3 shaft) || !TryFind(sceneText, "EntryApproach", out Vector3 entry)) return false;
        markers = new EmitterPoiMarkers(origin, crown, shaft, entry);
        return true;
    }

    private static bool TryFind(string text, string name, out Vector3 offset)
    {
        offset = Vector3.Zero;
        int at = text.IndexOf("[node name=\"" + name + "\"", StringComparison.Ordinal);
        if (at < 0) return false;
        int end = text.IndexOf("[node", at + 1, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        int t = text.IndexOf("Transform3D(", at, end - at, StringComparison.Ordinal);
        if (t < 0) return true; // identity transform is omitted from the scene file
        int close = text.IndexOf(')', t);
        string[] parts = text.Substring(t + 12, close - t - 12).Split(',');
        if (parts.Length != 12) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        offset = new Vector3(float.Parse(parts[9], ci), float.Parse(parts[10], ci), float.Parse(parts[11], ci));
        return true;
    }
}

/// <summary>
/// Per-snapshot constants for mapping canonical (around, vertical, across) to the current world frame. Holds everything in
/// double precision; the only float narrowing is the final Transform3D / Vector3. Mirrors
/// <see cref="RingLodGeometrySnapshot.CanonicalToWorld"/> (anchor-relative local, then grid basis plus origin) and
/// RingTerrain3DGameplay.BuildWorldFrame (basis columns tangent, inward, grid Down).
/// </summary>
public readonly struct EmitterSiteFrameContext
{
    private readonly double _b0x, _b0y, _b0z, _b1x, _b1y, _b1z, _b2x, _b2y, _b2z; // grid basis columns
    private readonly double _ox, _oy, _oz; // GridGlobalTransform.Origin - FloatingOriginWorldOffset
    private readonly double _cx, _cy, _cz; // RingCenterLocalOffset
    private readonly double _ax, _ay, _az; // anchor in absolute local (zero when no anchor)
    private readonly double _radius, _verticalBase, _acrossBase, _circumference;

    public EmitterSiteFrameContext(in RingLodGeometrySnapshot g)
    {
        Basis b = g.GridGlobalTransform.Basis;
        _b0x = b.X.X; _b0y = b.X.Y; _b0z = b.X.Z;
        _b1x = b.Y.X; _b1y = b.Y.Y; _b1z = b.Y.Z;
        _b2x = b.Z.X; _b2y = b.Z.Y; _b2z = b.Z.Z;
        Vector3 o = g.GridGlobalTransform.Origin - g.FloatingOriginWorldOffset;
        _ox = o.X; _oy = o.Y; _oz = o.Z;
        _cx = g.RingCenterLocalOffset.X; _cy = g.RingCenterLocalOffset.Y; _cz = g.RingCenterLocalOffset.Z;
        _radius = g.RingRadiusMeters; _verticalBase = g.RingVerticalBaseOffsetMeters;
        _acrossBase = g.RingAcrossBaseOffsetMeters; _circumference = g.CircumferenceMeters;
        _ax = _ay = _az = 0d;
        if (g.FloatingOriginCanonicalAnchor is { } anchor)
        {
            (_ax, _ay, _az) = g.CanonicalToAbsoluteLocal(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
        }
    }

    public double RingRadiusMeters => _radius;

    private Vector3D GridBasis(double x, double y, double z)
        => new(_b0x * x + _b1x * y + _b2x * z, _b0y * x + _b1y * y + _b2y * z, _b0z * x + _b1z * y + _b2z * z);

    private double Wrap(double around)
    {
        double a = around % _circumference;
        return a < 0d ? a + _circumference : a;
    }

    /// <summary>Anchor-relative local position (double) of a canonical point, as <c>CanonicalToRelativeLocal</c>.</summary>
    public Vector3D RelativeLocal(double around, double vertical, double across, out double cosPhi, out double sinPhi)
    {
        double angle = Wrap(around) / _radius;
        cosPhi = Math.Cos(angle); sinPhi = Math.Sin(angle);
        double radial = _radius - (_verticalBase + vertical);
        return new Vector3D(_cx + cosPhi * radial - _ax, _cy + _acrossBase + across - _ay, _cz + sinPhi * radial - _az);
    }

    /// <summary>World position (double) of a canonical point.</summary>
    public Vector3D WorldPosition(double around, double vertical, double across)
    {
        Vector3D rel = RelativeLocal(around, vertical, across, out _, out _);
        return GridBasis(rel.X, rel.Y, rel.Z) + new Vector3D(_ox, _oy, _oz);
    }

    /// <summary>World position (double) of the ring hub: radial distance 0 on the ring axis at the across coordinate <paramref name="hubAcrossMeters"/> (as <see cref="Beam"/>).</summary>
    public Vector3D HubWorldPosition(double hubAcrossMeters)
    {
        Vector3D rel = new(_cx - _ax, _cy + _acrossBase + hubAcrossMeters - _ay, _cz - _az);
        return GridBasis(rel.X, rel.Y, rel.Z) + new Vector3D(_ox, _oy, _oz);
    }

    /// <summary>
    /// Terrain3D world-frame basis columns at ring angle phi (RingTerrain3DGameplay.BuildWorldFrame): tangent (+around),
    /// inward (local up toward the hub) and grid Down (= -across, right-handed because AcrossDirection = -1).
    /// </summary>
    public void SurfaceAxes(double cosPhi, double sinPhi, out Vector3D tangent, out Vector3D inward, out Vector3D minusAcross)
    {
        tangent = GridBasis(-sinPhi, 0d, cosPhi).Normalized();
        inward = GridBasis(-cosPhi, 0d, -sinPhi).Normalized();
        minusAcross = GridBasis(0d, -1d, 0d).Normalized();
    }

    /// <summary>
    /// Site transform: origin at canonical (around, vertical, across), basis = surface frame * yaw about local up, where
    /// <paramref name="vertical"/> already includes the pad clearance and yaw is given as cos/sin of theta = 67.5 deg + beta.
    /// </summary>
    public Transform3D SiteTransform(double around, double across, double vertical, double cosYaw, double sinYaw)
    {
        Vector3D rel = RelativeLocal(around, vertical, across, out double c, out double s);
        Vector3D origin = GridBasis(rel.X, rel.Y, rel.Z) + new Vector3D(_ox, _oy, _oz);
        SurfaceAxes(c, s, out Vector3D t, out Vector3D up, out Vector3D z);
        // frame * Basis(Up, theta): columns (cos t, 0, -sin t), (0,1,0), (sin t, 0, cos t) in frame space.
        Vector3D x = t * cosYaw - z * sinYaw;
        Vector3D zz = t * sinYaw + z * cosYaw;
        return new Transform3D(new Basis(x.ToVector3(), up.ToVector3(), zz.ToVector3()), origin.ToVector3());
    }

    /// <summary>Eclipse beam geometry for one site (U2: aimed exactly at the ring hub at <paramref name="hubAcrossMeters"/>).</summary>
    public EmitterBeamGeometry Beam(double around, double across, double padHeightWithClearance, double crownHeightMeters, double hubAcrossMeters)
    {
        double vertical = padHeightWithClearance + crownHeightMeters;
        Vector3D rel = RelativeLocal(around, vertical, across, out double c, out double s);
        // Hub: ring axis (radial distance 0) at the hub across coordinate.
        double hubX = _cx - _ax, hubY = _cy + _acrossBase + hubAcrossMeters - _ay, hubZ = _cz - _az;
        Vector3D toHubLocal = new(hubX - rel.X, hubY - rel.Y, hubZ - rel.Z);
        Vector3D dir = GridBasis(toHubLocal.X, toHubLocal.Y, toHubLocal.Z).Normalized();
        Vector3D up = GridBasis(-c, 0d, -s).Normalized();
        Vector3D origin = GridBasis(rel.X, rel.Y, rel.Z) + new Vector3D(_ox, _oy, _oz);
        double tilt = Math.Acos(Math.Clamp(dir.Dot(up), -1d, 1d)) * (180d / Math.PI);
        return new EmitterBeamGeometry(origin.ToVector3(), dir, up, tilt, toHubLocal.Length);
    }
}

/// <summary>Where a spire to hub beam rises from (docs/eclipse-false-sun-spec.md, tweaks plan R4): the crown focus, the bore iris at the spire base, or the reactor exhaust top.</summary>
public enum EmitterBeamOrigin : byte { Crown = 0, Base = 1, Reactor = 2 }

/// <summary>Stateless site frame helpers (docs/emitter-poi-streaming-spec.md section A.2 and B.7).</summary>
public static class EmitterSiteFrameMath
{
    public const double DefaultPadClearanceMeters = 0.05;
    public const double DefaultCrownHeightMeters = 1743d; // BeamCrownFocus marker in BeamEmitterPOI.tscn
    public const double DefaultMainCrownHeightMeters = 3989d; // BeamCrownFocus marker in MainSpirePOI.tscn (spire is 4,000 m tall); no tilt, it stands on the hub line

    /// <summary>
    /// Height of a hub beam's origin above the pad datum (metres, negative below ground). Crown = the pad clearance plus the crown focus
    /// (1,743 / 3,989 m), exactly the old beam origin; Base = the bore iris, <see cref="EmitterSiteApertures.PlugDepthM"/> (200 m) under the pad, no clearance;
    /// Reactor = the reactor exhaust top (-2,961 m regular, -1,922 m main), where the shaft rises from.
    /// </summary>
    public static double BeamOriginHeightMeters(SiteKind kind, EmitterBeamOrigin origin) => origin switch
    {
        EmitterBeamOrigin.Base => -(double)EmitterSiteApertures.PlugDepthM,
        EmitterBeamOrigin.Reactor => EmitterSiteUnderground.Shape(kind).ExhaustTopYM,
        _ => DefaultPadClearanceMeters + (kind == SiteKind.Main ? DefaultMainCrownHeightMeters : DefaultCrownHeightMeters)
    };

    /// <summary>
    /// Site Transform3D in the current world frame: basis = Terrain3D world frame at the site's ring angle (tangent, inward,
    /// grid Down; AcrossDirection = -1) rotated by yaw theta about local up; origin = snapshot.CanonicalToWorld(A, P + clearance, X).
    /// Depends only on the placement and the current snapshot, never on previous transforms.
    /// </summary>
    public static Transform3D ComputeSiteTransform(in RingLodGeometrySnapshot snapshot, in EmitterSitePlacement p, double padClearanceMeters = DefaultPadClearanceMeters)
        => new EmitterSiteFrameContext(snapshot).SiteTransform(p.AroundMeters, p.AcrossMeters, p.PadHeightMeters + padClearanceMeters, Math.Cos(p.YawRadians), Math.Sin(p.YawRadians));

    /// <summary>The un-yawed surface frame (tangent, inward, grid Down) at an around coordinate: what Terrain3D's BuildWorldFrame builds.</summary>
    public static Basis ComputeSurfaceBasis(in RingLodGeometrySnapshot snapshot, double aroundMeters)
    {
        var context = new EmitterSiteFrameContext(snapshot);
        double angle = (aroundMeters % snapshot.CircumferenceMeters) / snapshot.RingRadiusMeters;
        context.SurfaceAxes(Math.Cos(angle), Math.Sin(angle), out Vector3D t, out Vector3D up, out Vector3D z);
        return new Basis(t.ToVector3(), up.ToVector3(), z.ToVector3());
    }

    /// <summary>
    /// Beam origin (bore centre at crown height) and the unit direction to the ring hub, plus the tilt relative to the spire axis.
    /// <paramref name="hubAcrossMeters"/> is the across coordinate of the hub (ring width / 2).
    /// </summary>
    public static EmitterBeamGeometry GetBeamGeometry(in RingLodGeometrySnapshot snapshot, in EmitterSitePlacement p, double hubAcrossMeters,
        double crownHeightMeters = DefaultCrownHeightMeters, double padClearanceMeters = DefaultPadClearanceMeters)
        => new EmitterSiteFrameContext(snapshot).Beam(p.AroundMeters, p.AcrossMeters, p.PadHeightMeters + padClearanceMeters, crownHeightMeters, hubAcrossMeters);

    /// <summary>
    /// Snapshot-free tilt (degrees) of a hub-converging beam, from canonical numbers only:
    /// atan(|across - hubAcross| / radial distance from the hub to the crown).
    /// </summary>
    public static double GetBeamTiltDegrees(double acrossMeters, double hubAcrossMeters, double radiusMeters, double verticalBaseOffsetMeters, double crownVerticalMeters)
        => Math.Atan2(Math.Abs(acrossMeters - hubAcrossMeters), radiusMeters - (verticalBaseOffsetMeters + crownVerticalMeters)) * (180d / Math.PI);
}
