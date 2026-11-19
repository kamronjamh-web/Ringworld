using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Test helper of tools/near_fog_shadow_gpu_test.gd (NF-5): installs a synthetic near cloud-shadow cascade into the static RingCloudShadowProjector (GDScript cannot call static C# methods).
/// One half plane of the ring-less flat frame (across &gt; 0) is shadow (R = 1), the other clear; strength is the projector's. Every registered receiver (the ground, the near fog) reads it exactly as it reads Sunshine's cascade.
/// </summary>
public partial class NearFogShadowProbe : RefCounted
{
    private ImageTexture? _texture;

    /// <summary>Installs the cascade: centre/axes in world space, radius m (the texture covers 2 x radius), shadow = true shades the across &gt; 0 half, false installs a fully clear map (same texture format, so only the values differ).</summary>
    public void Install(Vector3 center, Vector3 around, Vector3 across, Vector3 up, float radius, float strength, bool shadow)
    {
        const int size = 512;
        var bytes = new byte[size * size * 2 * sizeof(float)];
        var one = System.BitConverter.GetBytes(1.0f);
        for (int y = size / 2; y < size && shadow; y++)
            for (int x = 0; x < size; x++)
                System.Buffer.BlockCopy(one, 0, bytes, (y * size + x) * 2 * sizeof(float), sizeof(float));
        _texture = ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgf, bytes));
        RingCloudShadowProjector.UpdateCascades(_texture, center, around, across, up, radius, Vector2.Zero,
            null, center, around, across, up, radius, Vector2.Zero, 0.0f, Vector3.Zero, strength);
    }

    public void Remove()
    {
        RingCloudShadowProjector.ReleaseTextures();
        _texture = null;
    }
}
