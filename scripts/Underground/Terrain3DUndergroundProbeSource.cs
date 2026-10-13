using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Stub for the surface_runtime_generation_v1 (Terrain3D) backend. Caves are not ported yet, so density is
/// "air above the surface height, solid below" and the probe reports every position as open until the cave field exists.
/// </summary>
public sealed class Terrain3DUndergroundProbeSource : IUndergroundProbeSource
{
    private readonly RingTerrain3DGameplay _gameplay;

    public Terrain3DUndergroundProbeSource(RingTerrain3DGameplay gameplay) => _gameplay = gameplay;

    public bool IsAvailable => Godot.GodotObject.IsInstanceValid(_gameplay) && _gameplay.IsNodeReady();

    // No public edit revision yet; the stub density is a pure function of the surface height.
    public ulong Revision => 0;

    public bool TryGetSurfaceHeight(double around, double across, out float height)
    {
        height = IsAvailable ? _gameplay.SampleCollisionHeight(around, across) : 0f;
        return IsAvailable && float.IsFinite(height);
    }

    public bool TryGetDensity(double around, double vertical, double across, out float density)
    {
        density = 0;
        if (!TryGetSurfaceHeight(around, across, out float surface)) return false;
        density = vertical < surface ? -1f : 1f;
        return true;
    }
}
