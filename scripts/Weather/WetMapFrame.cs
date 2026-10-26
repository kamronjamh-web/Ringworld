using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Pure frame of the wet map (docs/storm-revamp-s2-wet-world-design.md 2.1): 256 x 256 texels of 64 m, camera-centred and snapped to whole texels in canonical
/// ring coordinates (double), so the map is world-anchored (no swimming) and a floating-origin rebase never changes it. The shader-side world to map conversion
/// (<see cref="WorldFrame"/>, ring_wetness.gdshaderinc) mirrors ring_storm_level's arc correction; <see cref="WorldToMap"/> and <see cref="WorldToPlane"/> are its CPU twins.
/// Plane coordinates (puddle noise, ripples) are canonical metres modulo <see cref="PlaneModulusMeters"/> (2^14, divisible by every tile size), computed in double here.
/// </summary>
public readonly record struct WetMapFrame(double CornerAround, double CornerAcross, double CameraAround, double CameraAcross, double CircumferenceMeters)
{
    public const int Size = 256;
    public const float TexelMeters = 64.0f;
    public const float ExtentMeters = Size * TexelMeters;
    public const double PlaneModulusMeters = 16384.0;
    public const float NoiseTileMeters = 51.2f;
    public const float RippleTileMeters = 4.0f;

    public static WetMapFrame Create(double cameraAround, double cameraAcross, double circumferenceMeters)
    {
        double around = Wrap(cameraAround, circumferenceMeters);
        double cornerAround = Wrap((Math.Floor(around / TexelMeters) - Size / 2) * TexelMeters, circumferenceMeters);
        double cornerAcross = (Math.Floor(cameraAcross / TexelMeters) - Size / 2) * TexelMeters;
        return new WetMapFrame(cornerAround, cornerAcross, around, cameraAcross, circumferenceMeters);
    }

    /// <summary>Same texel window (corner), camera moved: used by tests and by the renderer between map refreshes.</summary>
    public WetMapFrame WithCamera(double cameraAround, double cameraAcross) => this with { CameraAround = Wrap(cameraAround, CircumferenceMeters), CameraAcross = cameraAcross };

    public bool SameWindow(in WetMapFrame other) => CornerAround == other.CornerAround && CornerAcross == other.CornerAcross;

    /// <summary>Camera offset from the map corner in (around, across) metres; around is wrap-aware and lies in [0, extent] while the camera is inside the window.</summary>
    public Vector2 CameraOffsetMeters => new((float)AroundDelta(CameraAround, CornerAround, CircumferenceMeters), (float)(CameraAcross - CornerAcross));

    /// <summary>Canonical position of a texel centre.</summary>
    public (double Around, double Across) TexelCentre(int x, int y)
        => (Wrap(CornerAround + (x + 0.5) * TexelMeters, CircumferenceMeters), CornerAcross + (y + 0.5) * TexelMeters);

    /// <summary>Camera around coordinate modulo the plane period (double, so mega-ring positions keep their noise phase).</summary>
    public float PlaneAroundMeters => (float)Mod(CameraAround, PlaneModulusMeters);
    public float PlaneAcrossMeters => (float)Mod(CameraAcross, PlaneModulusMeters);

    public static double Wrap(double value, double period)
    {
        if (period <= 0.0) return value;
        double v = value % period;
        return v < 0.0 ? v + period : v;
    }

    public static double Mod(double value, double period)
    {
        double v = value % period;
        return v < 0.0 ? v + period : v;
    }

    public static double AroundDelta(double a, double b, double circumference)
    {
        double d = a - b;
        if (circumference > 0.0) { d %= circumference; if (d > circumference * 0.5) d -= circumference; else if (d < -circumference * 0.5) d += circumference; }
        return d;
    }

    /// <summary>
    /// The shader frame (ring_wet_frame): columns 0/1 = around/across axis.xyz and the camera's canonical around/across modulo the plane period, column 2 = up axis.xyz
    /// and the ring radius (from the ring centre line, as ringworld_storm_frame), column 3 = the camera-ring centre in world. Axes are analytic from the ring angle.
    /// </summary>
    public Projection WorldFrame(in RingLodGeometrySnapshot geometry)
    {
        Vector3 centre = geometry.CanonicalToWorld(CameraAround, 0.0, CameraAcross);
        Vector3 around = (geometry.CanonicalToWorld(CameraAround + 10.0, 0.0, CameraAcross) - centre).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(CameraAround, 0.0, CameraAcross + 10.0) - centre).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(CameraAround, 10.0, CameraAcross) - centre).Normalized();
        float radius = (float)Math.Max(1.0, geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters);
        return new Projection(
            new Vector4(around.X, around.Y, around.Z, PlaneAroundMeters),
            new Vector4(across.X, across.Y, across.Z, PlaneAcrossMeters),
            new Vector4(up.X, up.Y, up.Z, radius),
            new Vector4(centre.X, centre.Y, centre.Z, 0.0f));
    }

    static float ArcAngle(float tangent, float outward)
    {
        float q = tangent / outward;
        if (outward > 0.0f && MathF.Abs(q) <= 0.05f)
        {
            float q2 = q * q;
            return q * (1.0f - q2 * (1.0f / 3.0f - q2 * (1.0f / 5.0f - q2 * (1.0f / 7.0f))));
        }
        return MathF.Atan2(tangent, outward);
    }

    /// <summary>Arc-corrected (around, across) metres of a world point relative to the camera (the shader's offset).</summary>
    public static Vector2 WorldOffsetFromCamera(Vector3 world, in Projection frame)
    {
        Vector3 relative = world - new Vector3(frame.W.X, frame.W.Y, frame.W.Z);
        float tangent = relative.Dot(new Vector3(frame.X.X, frame.X.Y, frame.X.Z));
        float across = relative.Dot(new Vector3(frame.Y.X, frame.Y.Y, frame.Y.Z));
        float inward = relative.Dot(new Vector3(frame.Z.X, frame.Z.Y, frame.Z.Z));
        float radius = frame.Z.W;
        float arc = radius > 1.0f ? radius * ArcAngle(tangent, radius - inward) : tangent;
        return new Vector2(arc, across);
    }

    /// <summary>Map position in metres from the map corner (the shader's wet-map uv times the extent).</summary>
    public Vector2 WorldToMap(Vector3 world, in Projection frame) => CameraOffsetMeters + WorldOffsetFromCamera(world, frame);

    /// <summary>World-anchored plane metres modulo <see cref="PlaneModulusMeters"/> for noise and ripples (ring_wet_plane).</summary>
    public static Vector2 WorldToPlane(Vector3 world, in Projection frame) => new Vector2(frame.X.W, frame.Y.W) + WorldOffsetFromCamera(world, frame);
}
