using System;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Per-window emitter-site hole mask for the Terrain3D gameplay window. A pure function of (registry, frame): computed in
/// the pack job, never cached and never part of the save or edit layer. A vertex is a hole iff
/// <c>du^2 + dw^2 &lt; R^2</c> in exact integer lattice metres around a site (R 80 regular, 155 main). Thread-safe.
/// </summary>
public static class RingTerrain3DApertureMask
{
    /// <summary>Terrain3D control-map bit 2 (the ring shader reads it in the vertex pass).</summary>
    public const uint HoleBit = 1u << 2;
    /// <summary>Terrain3D's default control texel (auto-shaded); measured against a fresh region by the hole test.</summary>
    public const uint DefaultControlBits = 0x1;
    private const int Side = 384, Anchor = 128, Spacing = 2, RegionSide = 128;

    /// <summary>
    /// Returns a 384 x 384 row-major (z * 384 + x) mask, 1 = hole, or null when the registry is null or no aperture reaches the window.
    /// Vertex (x, z) sits at local metres ((x - 128) * 2, (z - 128) * 2) from the frame anchor, matching the height window.
    /// </summary>
    public static byte[]? Build(EmitterSiteRegistry? registry, in RingSurfaceFrame frame)
    {
        if (registry == null) return null;
        long centreAround = frame.AnchorAroundMeters + 127;
        double centreAcross = frame.AnchorAcrossMeters + frame.AcrossDirection * 127.0;
        // Circumscribed circle of the 766 m window; apertures are matched exactly by the rasteriser below.
        Span<SurfaceAperture> apertures = stackalloc SurfaceAperture[16];
        int count = EmitterSiteApertures.GetSurfaceApertures(registry, centreAround, centreAcross, 543.0, apertures);
        SurfaceAperture[]? heap = null;
        if (count > apertures.Length)
        {
            heap = new SurfaceAperture[count];
            count = EmitterSiteApertures.GetSurfaceApertures(registry, centreAround, centreAcross, 543.0, heap);
        }
        ReadOnlySpan<SurfaceAperture> list = heap != null ? heap.AsSpan(0, count) : apertures.Slice(0, count);
        byte[]? mask = null;
        foreach (SurfaceAperture aperture in list)
        {
            long sx = frame.SignedAroundDeltaMeters(aperture.Around);
            long sz = (long)frame.AcrossDirection * (aperture.Across - (long)frame.AnchorAcrossMeters);
            long radius = (long)aperture.Radius, radiusSquared = radius * radius;
            int x0 = (int)Math.Max(0, FloorDiv(sx - radius, Spacing) + Anchor), x1 = (int)Math.Min(Side - 1, FloorDiv(sx + radius, Spacing) + 1 + Anchor);
            int z0 = (int)Math.Max(0, FloorDiv(sz - radius, Spacing) + Anchor), z1 = (int)Math.Min(Side - 1, FloorDiv(sz + radius, Spacing) + 1 + Anchor);
            for (int z = z0; z <= z1; z++)
            {
                long dz = (long)(z - Anchor) * Spacing - sz;
                for (int x = x0; x <= x1; x++)
                {
                    long dx = (long)(x - Anchor) * Spacing - sx;
                    if (dx * dx + dz * dz >= radiusSquared) continue;
                    mask ??= new byte[Side * Side];
                    mask[z * Side + x] = 1;
                }
            }
        }
        return mask;
    }

    /// <summary>Nine RF 128 x 128 control images (z-major like the height images); texel = default | (hole ? HoleBit : 0) as raw uint bits.</summary>
    public static Array<Image> CreateControlImages(byte[] mask)
    {
        if (mask == null || mask.Length != Side * Side) throw new ArgumentException("Hole mask must be 384 x 384.", nameof(mask));
        var images = new Array<Image>();
        try
        {
            for (int rz = 0; rz < 3; rz++) for (int rx = 0; rx < 3; rx++)
            {
                byte[] data = new byte[RegionSide * RegionSide * 4];
                for (int row = 0; row < RegionSide; row++)
                {
                    int source = (rz * RegionSide + row) * Side + rx * RegionSide;
                    for (int column = 0; column < RegionSide; column++)
                        BitConverter.TryWriteBytes(data.AsSpan((row * RegionSide + column) * 4, 4), DefaultControlBits | (mask[source + column] != 0 ? HoleBit : 0u));
                }
                images.Add(Image.CreateFromData(RegionSide, RegionSide, false, Image.Format.Rf, data));
            }
            return images;
        }
        catch { foreach (Image image in images) image.Dispose(); throw; }
    }

    /// <summary>A region-sized control image holding only the default texel; used to clear a reused region.</summary>
    public static Image CreateBlankControlImage()
    {
        byte[] data = new byte[RegionSide * RegionSide * 4];
        for (int i = 0; i < RegionSide * RegionSide; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), DefaultControlBits);
        return Image.CreateFromData(RegionSide, RegionSide, false, Image.Format.Rf, data);
    }

    public static int CountHoles(byte[]? mask)
    {
        if (mask == null) return 0;
        int count = 0;
        foreach (byte value in mask) if (value != 0) count++;
        return count;
    }

    private static long FloorDiv(long value, long divisor) => (long)Math.Floor((double)value / divisor);
}
