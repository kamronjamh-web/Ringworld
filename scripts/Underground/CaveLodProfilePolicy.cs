namespace RingworldTechDemo.Underground;

/// <summary>
/// Decides when the cave LOD profile is applied. Underground (or entering) applies it at once; returning to the
/// surface keeps it for a shrink delay so brief exits do not thrash the coarse ring. Pure logic, no engine types.
/// </summary>
public sealed class CaveLodProfilePolicy
{
    public bool Active { get; private set; }
    public double SurfaceSeconds { get; private set; }

    public CaveLodProfile? Step(double delta, UndergroundMode mode, bool disabled, in CaveLodProfile underground, float shrinkDelaySeconds)
    {
        if (disabled) { Active = false; SurfaceSeconds = 0; return null; }
        if (mode != UndergroundMode.Surface) { Active = true; SurfaceSeconds = 0; }
        else if (Active)
        {
            SurfaceSeconds += delta;
            if (SurfaceSeconds >= System.Math.Max(0f, shrinkDelaySeconds)) { Active = false; SurfaceSeconds = 0; }
        }
        return Active ? underground : null;
    }
}
