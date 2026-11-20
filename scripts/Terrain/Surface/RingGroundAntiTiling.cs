using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Debug and test A/B switch for the ground albedo anti-tiling (materials/ring_biome_antitile.gdshaderinc). It writes the shader global
/// <c>ringworld_ground_antitile</c>: 1 = on (shipped, the project.godot default), 0 = the plain single tap. Not a setting: there is no menu row.
/// </summary>
public static class RingGroundAntiTiling
{
    public const string GlobalName = "ringworld_ground_antitile";

    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            RenderingServer.GlobalShaderParameterSet(GlobalName, value ? 1.0f : 0.0f);
        }
    }
}
