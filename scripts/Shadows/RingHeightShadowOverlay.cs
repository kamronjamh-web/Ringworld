using Godot;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Transient (never saved) 256 px top-down debug view of the height-shadow globals (materials/shadows/ring_height_shadow_debug_overlay.gdshader).
/// Modes: 0 level-0 surface visibility, 1 level-1 surface visibility, 2 blocker distance, 3 unresolved, 4 window bounds. The shader samples exactly what a
/// receiver would (frame matrix, bounds, params, front layer), so a wrong global shows up here before S5 reads it.
/// </summary>
public sealed partial class RingHeightShadowOverlay : ColorRect
{
    public const string ShaderPath = "res://materials/shadows/ring_height_shadow_debug_overlay.gdshader";
    public const int PanelSize = 256;
    public static readonly string[] ModeNames = { "L0 surface visibility", "L1 surface visibility", "Blocker distance", "Unresolved", "Window bounds" };
    private ShaderMaterial? _material;
    public int Mode { get; set; }
    public float ExtentMeters { get; private set; } = 4096f;

    public RingHeightShadowOverlay()
    {
        Name = "HeightShadowOverlay";
        CustomMinimumSize = new Vector2(PanelSize, PanelSize);
        Size = new Vector2(PanelSize, PanelSize);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        var shader = GD.Load<Shader>(ShaderPath);
        _material = new ShaderMaterial { Shader = shader };
        Material = _material;
    }

    public override void _Process(double delta)
    {
        if (_material == null || !IsVisibleInTree()) return;
        RingTerrainHeightShadow? node = RingTerrainHeightShadow.Instance;
        if (node == null || !GodotObject.IsInstanceValid(node) || !node.TryGetOverlayView(out Vector3 origin, out Vector3 axisU, out Vector3 axisV)) return;
        int level = Mode is 1 or 4 ? 1 : 0;
        int texel = node.FrontTexelMeters(level);
        ExtentMeters = (texel > 0 ? texel : 4) * 1024f * 1.05f;
        _material.SetShaderParameter("view_origin", origin);
        _material.SetShaderParameter("axis_u", axisU);
        _material.SetShaderParameter("axis_v", axisV);
        _material.SetShaderParameter("extent_m", ExtentMeters);
        _material.SetShaderParameter("mode", Mode);
    }
}
