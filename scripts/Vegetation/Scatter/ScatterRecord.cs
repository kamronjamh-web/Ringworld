using System;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// T5 design A6: one scatter record, 44 bytes on disk. Positions are canonical integer centimetres, so they survive a file round trip unchanged.
/// <c>UpOct</c> is the record's up vector, octahedral, two snorm16, in the local ring frame (x around, y up, z across). <c>BaseTint</c> is RGB8 with the top byte 0.
/// </summary>
internal readonly record struct ScatterRecord(
    ulong Id,
    long AroundCm,
    int AcrossCm,
    float SurfaceHeight,
    ushort ClassId,
    ushort Mesh,
    float Size,
    float Yaw,
    uint UpOct,
    uint BaseTint)
{
    public const int DiskBytes = 44;

    private static double Sign(double v) => v < 0.0 ? -1.0 : 1.0;

    /// <summary>Octahedral encode of a direction in the local frame (x around, y up, z across); y is the hemisphere axis, so near-up vectors keep the most precision.</summary>
    public static uint EncodeUpOct(double x, double y, double z)
    {
        double l1 = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
        if (!(l1 > 1e-12)) { x = 0.0; y = 1.0; z = 0.0; l1 = 1.0; }
        double u = x / l1, v = z / l1;
        if (y < 0.0)
        {
            double fu = (1.0 - Math.Abs(v)) * Sign(u), fv = (1.0 - Math.Abs(u)) * Sign(v);
            u = fu; v = fv;
        }
        short su = (short)Math.Round(Math.Clamp(u, -1.0, 1.0) * 32767.0), sv = (short)Math.Round(Math.Clamp(v, -1.0, 1.0) * 32767.0);
        return (uint)(ushort)su | ((uint)(ushort)sv << 16);
    }

    public static (double X, double Y, double Z) DecodeUpOct(uint packed)
    {
        double u = (short)(ushort)(packed & 0xFFFFu) / 32767.0, v = (short)(ushort)(packed >> 16) / 32767.0;
        double y = 1.0 - Math.Abs(u) - Math.Abs(v);
        double x = u, z = v;
        if (y < 0.0)
        {
            x = (1.0 - Math.Abs(v)) * Sign(u);
            z = (1.0 - Math.Abs(u)) * Sign(v);
        }
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (!(len > 1e-12)) return (0.0, 1.0, 0.0);
        return (x / len, y / len, z / len);
    }

    /// <summary>RGB8, top byte 0. Inputs are linear 0-1 floats.</summary>
    public static uint PackBaseTint(float r, float g, float b)
    {
        static uint Q(float v) => (uint)Math.Clamp((int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f), 0, 255);
        return (Q(r) << 16) | (Q(g) << 8) | Q(b);
    }

    public static (float R, float G, float B) UnpackBaseTint(uint packed)
        => (((packed >> 16) & 0xFFu) / 255f, ((packed >> 8) & 0xFFu) / 255f, (packed & 0xFFu) / 255f);
}
