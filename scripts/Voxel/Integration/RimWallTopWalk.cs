using Godot;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>
/// Pure movement helper for walking on top of a rim wall (WO12). The wall occupies across [-D, 0] (side 0) and [W, W + D] (side 1) from the structural
/// bottom up to the wall top, so its top is a flat cap at the wall-top altitude. Canonical vectors are (around, vertical, across). No scene access.
/// </summary>
public static class RimWallTopWalk
{
    /// <summary>True when the position is over a rim wall body: across in [-D, 0) (side 0) or (W, W + D] (side 1), at or above the structural bottom.</summary>
    public static bool TryResolveSide(double across, double vertical, double widthMeters, double depthMeters, double structuralBottom, out int side)
    {
        side = -1;
        if (depthMeters <= 0.0 || vertical < structuralBottom) return false;
        if (across >= -depthMeters && across < 0.0) { side = 0; return true; }
        if (across > widthMeters && across <= widthMeters + depthMeters) { side = 1; return true; }
        return false;
    }

    /// <summary>
    /// One physics step on the wall: lifts out of the solid body, integrates, keeps the body on the cap (the outer edge is the vacuum side, the inner edge
    /// the 10 km drop to the habitat) and lands on the cap.
    /// </summary>
    public static void Step(ref double around, ref double vertical, ref double across, ref Vector3 velocity, double dt, int side,
        double widthMeters, double depthMeters, double wallTop, double halfBodyWidth, out bool grounded)
    {
        if (vertical < wallTop)
        {
            vertical = wallTop;
            velocity.Y = Mathf.Max(0.0f, velocity.Y);
        }

        around += velocity.X * dt;
        vertical += velocity.Y * dt;
        across += velocity.Z * dt;

        double low, high;
        if (side == 0)
        {
            low = -depthMeters + halfBodyWidth;
            high = -halfBodyWidth;
        }
        else
        {
            low = widthMeters + halfBodyWidth;
            high = widthMeters + depthMeters - halfBodyWidth;
        }
        if (across < low) { across = low; velocity.Z = 0.0f; }
        else if (across > high) { across = high; velocity.Z = 0.0f; }

        if (vertical <= wallTop)
        {
            vertical = wallTop;
            velocity.Y = Mathf.Max(0.0f, velocity.Y);
            grounded = true;
        }
        else
        {
            grounded = false;
        }
    }
}
