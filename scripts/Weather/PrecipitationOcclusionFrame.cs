using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Pure frame of the precipitation occlusion map (docs/storm-revamp-s1-design.md section 1). The frame lives in canonical ring coordinates
/// (double): the map centre snapped to 2 m in around/across and a plane height snapped to 16 m. World-space vectors are derived from the current
/// geometry snapshot, so a floating-origin rebase changes the world origin but never the frame (and never forces a re-render).
///
/// Image convention (camera basis X,Y,Z): Z = up (the camera looks along -Z, down), Y = around tangent (image up), X = Y x Z (image right, equals -across
/// on this ring). A world point maps to u = x/Extent + .5, v = .5 - y/Extent and depth = metres below the map plane; the R32F map stores the depth
/// of the first opaque surface below the plane (0 = nothing drawn = exposed).
/// </summary>
public readonly record struct PrecipitationOcclusionFrame(
    double CentreAround, double CentreAcross, double PlaneVertical, double ReferenceVertical, float PlaneHeightMeters)
{
    public const int Resolution = 512;
    public const float ExtentMeters = 128.0f;
    public const float HalfExtentMeters = ExtentMeters * 0.5f;
    public const float TexelMeters = ExtentMeters / Resolution;
    public const double SnapMeters = 2.0;
    public const double RecentreMeters = 16.0;
    public const double HeightQuantumMeters = 16.0;
    public const double MinPlaneHeightMeters = 40.0;
    public const double MaxPlaneHeightMeters = 1000.0;
    public const double FarMarginMeters = 64.0;
    /// <summary>The plane is rebuilt when the desired height drifts this far from the built one (cloud base moving, observer climbing).</summary>
    public const double HeightHysteresisMeters = 24.0;
    public const double VerticalRebuildMeters = 16.0;
    public const float NearMeters = 0.05f;

    /// <summary>Far plane distance from the map plane: the plane height above the observer plus the margin below them.</summary>
    public float FarMeters => PlaneHeightMeters + (float)FarMarginMeters;

    /// <summary>Plane height above the observer for a cloud base (clamped 40..1000 m). Non-finite bases fall back to 500 m.</summary>
    public static double DesiredHeight(double observerVertical, double cloudBaseMeters)
    {
        double height = double.IsFinite(cloudBaseMeters) ? cloudBaseMeters - observerVertical : 500.0;
        return Math.Clamp(height, MinPlaneHeightMeters, MaxPlaneHeightMeters);
    }

    public static PrecipitationOcclusionFrame Create(RingCanonicalPosition observer, double cloudBaseMeters, double circumferenceMeters)
    {
        double height = DesiredHeight(observer.VerticalMeters, cloudBaseMeters);
        double plane = Math.Round((observer.VerticalMeters + height) / HeightQuantumMeters) * HeightQuantumMeters;
        if (plane - observer.VerticalMeters < MinPlaneHeightMeters) plane += HeightQuantumMeters;
        double around = Snap(observer.AroundMeters);
        if (circumferenceMeters > 0) { around %= circumferenceMeters; if (around < 0) around += circumferenceMeters; }
        return new PrecipitationOcclusionFrame(around, Snap(observer.AcrossMeters), plane, observer.VerticalMeters, (float)(plane - observer.VerticalMeters));
    }

    public static double Snap(double meters) => Math.Round(meters / SnapMeters) * SnapMeters;

    /// <summary>Wrap-aware around distance on a circular ring.</summary>
    public static double AroundDelta(double a, double b, double circumferenceMeters)
    {
        double d = a - b;
        if (circumferenceMeters > 0) { d %= circumferenceMeters; if (d > circumferenceMeters * 0.5) d -= circumferenceMeters; else if (d < -circumferenceMeters * 0.5) d += circumferenceMeters; }
        return d;
    }

    /// <summary>True when this frame no longer fits the observer: recentre (more than 16 m from the centre), vertical drift, or a plane that no longer matches the cloud base.</summary>
    public bool NeedsRebuild(RingCanonicalPosition observer, double cloudBaseMeters, double circumferenceMeters)
    {
        if (Math.Abs(AroundDelta(observer.AroundMeters, CentreAround, circumferenceMeters)) > RecentreMeters
            || Math.Abs(observer.AcrossMeters - CentreAcross) > RecentreMeters) return true;
        if (Math.Abs(observer.VerticalMeters - ReferenceVertical) > VerticalRebuildMeters) return true;
        double desiredPlane = observer.VerticalMeters + DesiredHeight(observer.VerticalMeters, cloudBaseMeters);
        return Math.Abs(desiredPlane - PlaneVertical) > HeightHysteresisMeters || PlaneVertical - observer.VerticalMeters < MinPlaneHeightMeters * 0.5;
    }

    /// <summary>Camera basis in world space: Z = up, Y = around, X = Y x Z. Analytic from the ring angle, so it needs no floating-origin state.</summary>
    public static Basis CameraBasis(double centreAround, in RingLodGeometrySnapshot geometry)
    {
        double angle = centreAround % geometry.CircumferenceMeters;
        if (angle < 0) angle += geometry.CircumferenceMeters;
        angle /= geometry.RingRadiusMeters;
        Basis grid = geometry.GridGlobalTransform.Basis.Orthonormalized();
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        // CanonicalToAbsoluteLocal: radial distance = R - vertical, so vertical (up) points toward the ring centre.
        Vector3 up = (grid * new Vector3(-cos, 0, -sin)).Normalized();
        Vector3 around = (grid * new Vector3(-sin, 0, cos)).Normalized();
        Vector3 right = around.Cross(up).Normalized();
        return new Basis(right, around, up);
    }

    /// <summary>World position of the map plane centre under the given geometry (changes on every floating-origin rebase; the frame itself does not).</summary>
    public Vector3 WorldOrigin(in RingLodGeometrySnapshot geometry) => geometry.CanonicalToWorld(CentreAround, PlaneVertical, CentreAcross);

    public Transform3D WorldTransform(in RingLodGeometrySnapshot geometry) => new(CameraBasis(CentreAround, geometry), WorldOrigin(geometry));

    /// <summary>Projects a world point into the map: texel coordinates (pixel centres at +.5) and metres below the plane.</summary>
    public static void Project(Vector3 world, in Transform3D mapTransform, out Vector2 texel, out float depthBelowPlane)
    {
        Vector3 rel = world - mapTransform.Origin;
        Basis b = mapTransform.Basis;
        float x = rel.Dot(b.X), y = rel.Dot(b.Y);
        depthBelowPlane = -rel.Dot(b.Z);
        texel = new Vector2((x / ExtentMeters + 0.5f) * Resolution, (0.5f - y / ExtentMeters) * Resolution);
    }

    public static bool InsideMap(Vector2 texel, float marginTexels = 0) =>
        texel.X >= marginTexels && texel.Y >= marginTexels && texel.X < Resolution - marginTexels && texel.Y < Resolution - marginTexels;

    /// <summary>Height of the ring surface under the flat map plane at a lateral distance (single-basis frame sag): r^2 / (2R).</summary>
    public static double CurvatureSagMeters(double lateralMeters, double ringRadiusMeters) =>
        ringRadiusMeters - Math.Sqrt(Math.Max(0.0, ringRadiusMeters * ringRadiusMeters - lateralMeters * lateralMeters));
}
