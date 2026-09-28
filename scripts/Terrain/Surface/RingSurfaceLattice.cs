using System;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Shared canonical 2m terrain lattice interpolation used by runtime surface presentation and gameplay collision.</summary>
public static class RingSurfaceLattice
{
    /// <summary>
    /// Interpolates the canonical positive diagonal from (0,0) to (1,1).
    /// <paramref name="aroundFraction"/> advances around the ring and
    /// <paramref name="acrossFraction"/> advances across the ring.
    /// </summary>
    public static float TriangleInterpolate(
        float h00,
        float h10,
        float h01,
        float h11,
        double aroundFraction,
        double acrossFraction)
    {
        if (!float.IsFinite(h00) || !float.IsFinite(h10) || !float.IsFinite(h01) || !float.IsFinite(h11)
            || !double.IsFinite(aroundFraction) || !double.IsFinite(acrossFraction))
        {
            throw new ArgumentOutOfRangeException("Terrain lattice interpolation inputs must be finite.");
        }

        float tx = (float)Math.Clamp(aroundFraction, 0.0, 1.0);
        float tz = (float)Math.Clamp(acrossFraction, 0.0, 1.0);
        return tx >= tz
            ? h00 + (h10 - h00) * tx + (h11 - h10) * tz
            : h00 + (h11 - h01) * tx + (h01 - h00) * tz;
    }
}
