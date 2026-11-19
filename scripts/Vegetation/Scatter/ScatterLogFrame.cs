using System;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// T6-6 (A7 "Logs"): how a fallen-tree record lays its mesh. The mesh lies along its +X axis, centred on the record position. <see cref="RingScatterLayer.RecordPose"/> builds the rotation as
/// Basis(up, yaw) * Basis(x, up, x cross up), where x is the ring's around tangent with its up component removed, so a record with a tilted up and a yaw puts +X on
/// cos(yaw) x + sin(yaw) (up cross x). The builder (RingBiomeDecorationLayer.ScatterBuild.cs) picks the up and the yaw so that this axis is the chord between the two exact end heights; this class holds that
/// geometry in one place for the builder, the collider pool and the tests. All vectors are in the local ring frame (x around, y up, z across); that frame is left-handed, so the cross products run in (x, y, -z).
/// </summary>
internal static class ScatterLogFrame
{
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>The unit tangent x of RecordPose in the right-handed frame (around, up, -across): the around axis with its component along up removed.</summary>
    private static (double X, double Y, double Z) Tangent((double X, double Y, double Z) up)
    {
        var x = (1.0 - up.X * up.X, -up.Y * up.X, -up.Z * up.X);
        double l = Math.Sqrt(Dot(x, x));
        return l > 1e-9 ? (x.Item1 / l, x.Item2 / l, x.Item3 / l) : (0.0, 0.0, 1.0);
    }

    /// <summary>The yaw that puts the mesh's +X on <paramref name="axis"/> (a unit vector perpendicular to <paramref name="up"/>), both in the local ring frame.</summary>
    public static double YawFor(double ux, double uy, double uz, double ax, double ay, double az)
    {
        var up = (ux, uy, -uz);
        var axis = (ax, ay, -az);
        var x = Tangent(up);
        return Math.Atan2(Dot(axis, Cross(up, x)), Dot(axis, x));
    }

    /// <summary>The unit direction the record's mesh +X points along, in the local ring frame (x around, y up, z across), from the record's quantised up and its yaw.</summary>
    public static (double X, double Y, double Z) Axis(in ScatterRecord rec)
    {
        (double ux, double uy, double uz) = ScatterRecord.DecodeUpOct(rec.UpOct);
        var up = (ux, uy, -uz);
        var x = Tangent(up);
        var side = Cross(up, x);
        double c = Math.Cos(rec.Yaw), s = Math.Sin(rec.Yaw);
        var r = (x.Item1 * c + side.Item1 * s, x.Item2 * c + side.Item2 * s, x.Item3 * c + side.Item3 * s);
        return (r.Item1, r.Item2, -r.Item3);
    }

    /// <summary>The two ends of a log of the given length: canonical around and across metres and the height of the axis point, before any embed along up.</summary>
    public static ((double Around, double Across, double Height) End0, (double Around, double Across, double Height) End1) Ends(in ScatterRecord rec, double length)
    {
        (double ax, double ay, double az) = Axis(rec);
        double half = length * 0.5, around = rec.AroundCm * 0.01, across = rec.AcrossCm * 0.01;
        return ((around - half * ax, across - half * az, rec.SurfaceHeight - half * ay), (around + half * ax, across + half * az, rec.SurfaceHeight + half * ay));
    }
}
