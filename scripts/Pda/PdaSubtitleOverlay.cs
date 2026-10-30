using Godot;

namespace RingworldTechDemo.Pda;

/// <summary>Bottom-centre PDA subtitle line (the sink of <see cref="SubtitleOnlyVoiceOutput"/>); fades out after <see cref="HoldSeconds"/>.</summary>
public partial class PdaSubtitleOverlay : CanvasLayer
{
    public const double HoldSeconds = 7.0;

    private Label? _label;
    private double _remaining;

    public override void _Ready()
    {
        Layer = 90;
        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        _label.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _label.OffsetLeft = 160; _label.OffsetRight = -160; _label.OffsetTop = -150; _label.OffsetBottom = -96;
        _label.AddThemeFontSizeOverride("font_size", 22);
        _label.AddThemeColorOverride("font_color", new Color(0.82f, 0.95f, 1.0f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _label.AddThemeConstantOverride("outline_size", 6);
        AddChild(_label);
    }

    public void ShowLine(string line)
    {
        if (_label == null) return;
        _label.Text = "PDA: " + line;
        _label.Visible = true;
        _remaining = HoldSeconds;
    }

    public override void _Process(double delta)
    {
        if (_label == null || !_label.Visible) return;
        _remaining -= delta;
        if (_remaining <= 0.0) _label.Visible = false;
    }
}
