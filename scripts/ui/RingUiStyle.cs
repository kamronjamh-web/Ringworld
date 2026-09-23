using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Small shared presentation palette for runtime-created game UI.</summary>
public static class RingUiStyle
{
    public static readonly Color Ink = new(0.82f, 0.91f, 0.95f, 1.0f);
    public static readonly Color Muted = new(0.48f, 0.63f, 0.69f, 1.0f);
    public static readonly Color Accent = new(0.19f, 0.91f, 0.87f, 1.0f);
    public static readonly Color AccentBright = new(0.55f, 1.0f, 0.94f, 1.0f);
    public static readonly Color Danger = new(1.0f, 0.38f, 0.36f, 1.0f);

    public static Theme CreateTheme()
    {
        var theme = new Theme();
        theme.SetColor("font_color", "Label", Ink);
        theme.SetColor("font_shadow_color", "Label", new Color(0, 0, 0, 0.72f));
        theme.SetConstant("shadow_offset_x", "Label", 1);
        theme.SetConstant("shadow_offset_y", "Label", 2);
        theme.SetFontSize("font_size", "Label", 16);
        theme.SetColor("font_color", "Button", Ink);
        theme.SetColor("font_hover_color", "Button", AccentBright);
        theme.SetColor("font_pressed_color", "Button", new Color(0.02f, 0.08f, 0.09f));
        theme.SetColor("font_disabled_color", "Button", new Color(0.35f, 0.43f, 0.46f, 0.7f));
        theme.SetFontSize("font_size", "Button", 16);
        theme.SetStylebox("normal", "Button", ButtonBox(new Color(0.035f, 0.07f, 0.095f, 0.94f), new Color(0.16f, 0.46f, 0.5f, 0.7f)));
        theme.SetStylebox("hover", "Button", ButtonBox(new Color(0.045f, 0.14f, 0.16f, 0.98f), Accent));
        theme.SetStylebox("pressed", "Button", ButtonBox(Accent, AccentBright));
        theme.SetStylebox("focus", "Button", ButtonBox(new Color(0.04f, 0.11f, 0.13f, 0.98f), Accent));
        theme.SetStylebox("disabled", "Button", ButtonBox(new Color(0.025f, 0.035f, 0.045f, 0.72f), new Color(0.14f, 0.19f, 0.21f, 0.5f)));
        theme.SetColor("font_color", "CheckButton", Ink);
        theme.SetColor("font_color", "OptionButton", Ink);
        theme.SetStylebox("normal", "OptionButton", ButtonBox(new Color(0.025f, 0.055f, 0.073f, 0.98f), new Color(0.13f, 0.34f, 0.39f, 0.9f)));
        theme.SetStylebox("hover", "OptionButton", ButtonBox(new Color(0.04f, 0.11f, 0.13f, 0.98f), Accent));
        theme.SetStylebox("pressed", "OptionButton", ButtonBox(Accent, AccentBright));
        theme.SetStylebox("focus", "OptionButton", ButtonBox(new Color(0.04f, 0.11f, 0.13f, 0.98f), Accent));
        theme.SetStylebox("panel", "PanelContainer", GlassPanel());
        theme.SetStylebox("panel", "PopupPanel", GlassPanel(0.985f));
        return theme;
    }

    public static StyleBoxFlat GlassPanel(float alpha = 0.92f, int radius = 14)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.018f, 0.035f, 0.052f, alpha),
            BorderColor = new Color(0.16f, 0.52f, 0.55f, 0.68f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ShadowColor = new Color(0, 0, 0, 0.42f),
            ShadowSize = 12
        };
        return style;
    }

    public static StyleBoxFlat ButtonBox(Color fill, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 11,
            ContentMarginBottom = 11
        };
    }
}
