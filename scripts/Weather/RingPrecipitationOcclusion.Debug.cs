using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Debug overlay of the occlusion map (debug tooling, not the grave-key menu): Ctrl+F10 toggles a 512 px view of the published map in the top-left corner.
/// Colours are relative to the listener: red = a surface above the listener (cover, brighter = higher), green/blue = ground at or below the listener, magenta = nothing drawn.
/// The white cross is the listener and the text shows the frame, cadence and probe values.
/// </summary>
public partial class RingPrecipitationOcclusion
{
    private const string OverlayShaderCode = @"shader_type canvas_item;
uniform float listener_depth = 0.0;
void fragment() {
	float d = texture(TEXTURE, UV).r;
	vec3 c;
	if (d <= 0.0) c = vec3(0.85, 0.0, 0.85);
	else {
		float cover = listener_depth - d;
		if (cover > 0.3) c = mix(vec3(0.55, 0.12, 0.05), vec3(1.0, 0.9, 0.35), clamp(cover / 24.0, 0.0, 1.0));
		else c = mix(vec3(0.1, 0.55, 0.25), vec3(0.05, 0.2, 0.6), clamp(-cover / 24.0, 0.0, 1.0));
		c *= 1.0 - 0.25 * step(0.9, fract(d * 0.2));
	}
	COLOR = vec4(c, 1.0);
}";

    private const float OverlayPixels = 400f;
    private CanvasLayer? _overlay;
    private TextureRect? _overlayRect;
    private Label? _overlayLabel;
    private ColorRect _overlayCrossH = null!, _overlayCrossV = null!;
    private ShaderMaterial? _overlayMaterial;
    public bool DebugOverlayVisible => _overlay != null;

    public void SetDebugOverlay(bool visible)
    {
        if (!visible)
        {
            if (_overlay != null && IsInstanceValid(_overlay)) _overlay.QueueFree();
            _overlay = null; _overlayRect = null; _overlayLabel = null; _overlayMaterial = null;
            return;
        }
        if (_overlay != null || !IsInsideTree()) return;
        _overlay = new CanvasLayer { Name = "PrecipitationOcclusionDebug", Layer = 120 };
        _overlayMaterial = new ShaderMaterial { Shader = new Shader { Code = OverlayShaderCode } };
        _overlayRect = new TextureRect { Position = new Vector2(8, 8), Size = new Vector2(OverlayPixels, OverlayPixels), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, Material = _overlayMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlayLabel = new Label { Position = new Vector2(8, OverlayPixels + 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlayLabel.AddThemeColorOverride("font_color", Colors.White);
        _overlayLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _overlayLabel.AddThemeConstantOverride("outline_size", 4);
        _overlayCrossH = new ColorRect { Color = Colors.White, Size = new Vector2(15, 1), MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlayCrossV = new ColorRect { Color = Colors.White, Size = new Vector2(1, 15), MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.AddChild(_overlayRect); _overlay.AddChild(_overlayLabel); _overlay.AddChild(_overlayCrossH); _overlay.AddChild(_overlayCrossV);
        AddChild(_overlay);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10, CtrlPressed: true })
        {
            SetDebugOverlay(_overlay == null);
            GetViewport().SetInputAsHandled();
        }
    }

    private void UpdateDebugOverlay()
    {
        if (_overlay == null || _overlayRect == null || _overlayLabel == null || _overlayMaterial == null) return;
        _overlayRect.Texture = PublishedTexture;
        Vector3? listener = ProbeListenerWorldOverride ?? ObserverWorld();
        float depth = 0;
        Vector2 cross = new(-100, -100);
        if (Ready && listener != null)
        {
            PrecipitationOcclusionFrame.Project(listener.Value, PublishedTransform, out Vector2 texel, out depth);
            cross = new Vector2(8, 8) + texel * (OverlayPixels / PrecipitationOcclusionFrame.Resolution);
        }
        _overlayMaterial.SetShaderParameter("listener_depth", depth);
        _overlayCrossH.Position = cross - new Vector2(7, 0); _overlayCrossV.Position = cross - new Vector2(0, 7);
        _overlayLabel.Text = $"precip occlusion ready={Ready} serial={FrameSerial} renders={RenderCount} last={LastRenderReason} plane={(Ready ? PublishedFarMeters - PrecipitationOcclusionFrame.FarMarginMeters : 0):0}m\n"
            + $"probe fresh={IsFresh} coverHeight={ListenerCoverHeightMeters:0.0} open={ListenerOpenFraction:0.00} coverDepth={ListenerCoverDepthMeters:0.0}";
    }
}
