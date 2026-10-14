using System;

namespace RingworldTechDemo.Diagnostics;

/// <summary>
/// Cheap per-site counters for shader parameter writes. Godot queues a material
/// update for every set_shader_parameter call, so these show which owners still
/// write every frame. Main thread only; the monitor samples per-frame deltas.
/// </summary>
public static class RingMaterialWriteCounters
{
    public enum Site
    {
        FarSurfaceTileApply,
        FarSurfaceTileTemplateSync,
        FarRayFrameRegional,
        FarRayFrameWholeRing,
        FarLightning,
        FarMisc,
        CloudShadowProjector,
        WaterWind,
        Count
    }

    private static readonly long[] Writes = new long[(int)Site.Count];
    private static readonly long[] Textures = new long[(int)Site.Count];
    public static int SiteCount => (int)Site.Count;

    public static void Record(Site site, int writes = 1, int textureWrites = 0)
    {
        Writes[(int)site] += writes;
        Textures[(int)site] += textureWrites;
    }

    public static void Snapshot(Span<long> writes, Span<long> textureWrites)
    {
        Writes.AsSpan(0, Math.Min(Writes.Length, writes.Length)).CopyTo(writes);
        Textures.AsSpan(0, Math.Min(Textures.Length, textureWrites.Length)).CopyTo(textureWrites);
    }
}
