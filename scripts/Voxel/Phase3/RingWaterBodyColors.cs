using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// River and lake water colours of the seeded terrain colour path (<see cref="RingTerrainVisuals.ResolveSeededColor"/>, used by the proxy and global meshes),
/// plus a turbidity. The defaults are the constants that path used before they were tunable, so at defaults every water pixel is unchanged. The live tuning
/// menu writes them through <c>RingGraphicsQualityController</c>; mesh builders read one immutable snapshot per call, so a worker never sees a half-written set.
/// </summary>
public static class RingWaterBodyColors
{
    public static readonly Color DefaultRiver = new(0.075f, 0.36f, 0.43f, 1.0f);
    public static readonly Color DefaultLake = new(0.045f, 0.29f, 0.36f, 1.0f);
    public const float DefaultTurbidity = 0.0f;
    /// <summary>Silt tone that turbidity blends river and lake water toward.</summary>
    public static readonly Color Silt = new(0.30f, 0.26f, 0.15f, 1.0f);

    public sealed record Snapshot(Color River, Color Lake, float Turbidity);

    private static volatile Snapshot _current = new(DefaultRiver, DefaultLake, DefaultTurbidity);

    public static Snapshot Current => _current;

    public static void Set(Color river, Color lake, float turbidity)
        => _current = new Snapshot(river, lake, Mathf.Clamp(turbidity, 0.0f, 1.0f));

    /// <summary>The <c>river_far_color</c> shader uniform value: the river colour with turbidity applied (vertex-colour space, like the seeded colours).</summary>
    public static Vector3 RiverFarColor(Snapshot tint)
    {
        Color c = Resolve(tint.River, tint.Turbidity);
        return new Vector3(c.R, c.G, c.B);
    }

    /// <summary>The colour of a river or lake before climate variation. Turbidity 0 returns the colour untouched.</summary>
    public static Color Resolve(Color color, float turbidity)
        => turbidity <= 0.0f ? color : color.Lerp(Silt, turbidity);
}
