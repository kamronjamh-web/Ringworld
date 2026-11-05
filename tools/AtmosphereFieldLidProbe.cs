using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.ContainmentField;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO9: exposes the CPU twin of the atmosphere lid (RingAtmosphereFieldLid) for the live runtime-surface world profile to
/// tools/atmosphere_field_lid_gpu_test.gd, which compares it with the GLSL lid (ring_haze_field_runs in ring_haze_shared.gdshaderinc) on the GPU.
/// </summary>
public partial class AtmosphereFieldLidProbe : RefCounted
{
    public const double RadiusMeters = 5_000_000.0;
    public const double FloorDepthMeters = 2_500.0;

    // The live runtime-surface world: habitat width 318 km, wall top 10.4 km, apex 40 km, roots 64 m outside the faces, 1.6 km skirt.
    private static readonly ContainmentFieldProfile World = new(318_000.0, 10_400.0, 40_000.0, -64.0, 318_064.0, 1_600.0);
    private static readonly RingAtmosphereFieldLid.Profile Packed = RingAtmosphereFieldLid.Profile.From(World).AsPackedFloats();

    public Vector4 Packed0 => RingAtmosphereFieldLid.Profile.From(World).Packed0;
    public Vector4 Packed1 => RingAtmosphereFieldLid.Profile.From(World).Packed1;

    public double Height(double across) => RingAtmosphereFieldLid.Height(Packed, across);

    /// <summary>The across in the left half of the habitat where the field height first reaches <paramref name="heightMeters"/> (bisection).</summary>
    public double AcrossAtHeight(double heightMeters)
    {
        double lo = 0.0, hi = 0.5 * World.WidthMeters;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Height(mid) < heightMeters) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// CPU runs for a batch. inputs = 8 floats per case (the layout the GPU kernel reads): camera height, camera across, ray length, want_second,
    /// ray dx (outward), dy (axis), dz (around tangent), pad. Returns 4 doubles per case (X0, X1, Z0, Z1).
    /// </summary>
    public double[] RunsBatch(float[] inputs)
    {
        int count = inputs.Length / 8;
        var result = new double[count * 4];
        for (int i = 0; i < count; i++)
        {
            int o = i * 8;
            double dx = inputs[o + 4], dy = inputs[o + 5], dz = inputs[o + 6];
            double inv = 1.0 / System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx *= inv; dy *= inv; dz *= inv;
            var runs = RingAtmosphereFieldLid.Compute(Packed, inputs[o + 2], inputs[o], inputs[o + 1], RadiusMeters, FloorDepthMeters,
                dx, dy, dx * dx + dz * dz, inputs[o + 3] > 0.5f);
            result[i * 4] = runs.X0;
            result[i * 4 + 1] = runs.X1;
            result[i * 4 + 2] = runs.Z0;
            result[i * 4 + 3] = runs.Z1;
        }
        return result;
    }
}
