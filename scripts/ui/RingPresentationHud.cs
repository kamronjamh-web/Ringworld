using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.UI;

/// <summary>Capture-safe player HUD. Engineering diagnostics remain available separately on F6.</summary>
public partial class RingPresentationHud : CanvasLayer
{
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export(PropertyHint.Range, "0.05,1.0,0.05")] public float StatusRefreshSeconds { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "1,30,1")] public float HintVisibleSeconds { get; set; } = 9.0f;

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private Control? _presentationRoot;
    private Control? _brandCard;
    private Control? _statusCard;
    private Control? _crosshair;
    private Control? _hintCard;
    private Control? _captureOverlay;
    private Label? _modeLabel;
    private Label? _coordinateLabel;
    private Label? _altitudeLabel;
    private Label? _captureKicker;
    private Label? _captureTitle;
    private Label? _captureSubtitle;
    private double _statusElapsed;
    private double _hintElapsed;
    private double _captureElapsed;
    private double _capturePrerollSeconds;
    private bool _cinematic;
    private bool _captureActive;

    public bool CinematicMode => _cinematic;
    public const double CaptureIntroSeconds = 3.5;

    public override void _Ready()
    {
        Layer = 30;
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        BuildHud();
        HideLegacyPresentationUi();
        ConfigureCapturePresentation();
        RefreshStatus();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
        if (key.Keycode == Key.F8)
        {
            SetCinematicMode(!_cinematic);
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.F1 && _hintCard != null && !_captureActive)
        {
            _hintElapsed = 0.0;
            _hintCard.Visible = !_hintCard.Visible;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_captureActive) UpdateCaptureIntro(delta);
        if (_cinematic || _captureActive) return;

        _hintElapsed += delta;
        if (_hintCard != null && _hintElapsed >= HintVisibleSeconds)
            _hintCard.Visible = false;

        _statusElapsed += delta;
        if (_statusElapsed < Math.Max(0.05, StatusRefreshSeconds)) return;
        _statusElapsed = 0.0;
        RefreshStatus();
    }

    public void SetCinematicMode(bool enabled)
    {
        _cinematic = enabled;
        if (_presentationRoot != null) _presentationRoot.Visible = !enabled;
        if (enabled && _captureOverlay != null) _captureOverlay.Visible = false;
    }

    private void BuildHud()
    {
        _presentationRoot = new Control
        {
            Name = "PresentationRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Theme = RingUiStyle.CreateTheme()
        };
        AddChild(_presentationRoot);
        _presentationRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _brandCard = CreatePanel(_presentationRoot, 24, 22, 348, 92);
        var brandMargin = CreateMargin(_brandCard, 16, 12, 16, 10);
        var brandColumn = new VBoxContainer();
        brandColumn.AddThemeConstantOverride("separation", 1);
        brandMargin.AddChild(brandColumn);
        var brand = new Label { Text = "◉  RINGWORLD / EXPLORATION BUILD" };
        brand.AddThemeFontSizeOverride("font_size", 14);
        brand.AddThemeColorOverride("font_color", RingUiStyle.AccentBright);
        brandColumn.AddChild(brand);
        var engineeringHint = new Label { Text = "F8 CINEMATIC  •  F6 DIAGNOSTICS  •  F1 CONTROLS" };
        engineeringHint.AddThemeFontSizeOverride("font_size", 10);
        engineeringHint.Modulate = RingUiStyle.Muted;
        brandColumn.AddChild(engineeringHint);

        _statusCard = CreatePanel(_presentationRoot, 24, -116, 390, -24, bottomAnchored: true);
        var statusMargin = CreateMargin(_statusCard, 16, 11, 16, 10);
        var statusColumn = new VBoxContainer();
        statusColumn.AddThemeConstantOverride("separation", 2);
        statusMargin.AddChild(statusColumn);
        _modeLabel = new Label { Text = "SURFACE MODE" };
        _modeLabel.AddThemeFontSizeOverride("font_size", 13);
        _modeLabel.AddThemeColorOverride("font_color", RingUiStyle.AccentBright);
        statusColumn.AddChild(_modeLabel);
        _coordinateLabel = new Label { Text = "ARC —  •  ACROSS —" };
        _coordinateLabel.AddThemeFontSizeOverride("font_size", 11);
        _coordinateLabel.Modulate = RingUiStyle.Muted;
        statusColumn.AddChild(_coordinateLabel);
        _altitudeLabel = new Label { Text = "ALT —  •  SPEED —" };
        _altitudeLabel.AddThemeFontSizeOverride("font_size", 11);
        statusColumn.AddChild(_altitudeLabel);

        _hintCard = CreatePanel(_presentationRoot, -285, -91, 285, -24, bottomAnchored: true, centerAnchored: true);
        var hintMargin = CreateMargin(_hintCard, 15, 9, 15, 9);
        var hint = new Label
        {
            Text = "WASD MOVE  •  SHIFT SPRINT  •  SPACE JUMP  •  V FLIGHT\nIN WATER: WASD SWIM  •  SPACE/E RISE  •  C/Q DIVE\nLMB REMOVE  •  RMB PLACE  •  M MAP  •  WHEEL FLIGHT SPEED",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.Modulate = RingUiStyle.Muted;
        hintMargin.AddChild(hint);

        _crosshair = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _presentationRoot.AddChild(_crosshair);
        _crosshair.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddCrosshairLine(-9, -1, -3, 1);
        AddCrosshairLine(3, -1, 9, 1);
        AddCrosshairLine(-1, -9, 1, -3);
        AddCrosshairLine(-1, 3, 1, 9);
        var center = MakeRect(RingUiStyle.AccentBright, -1, -1, 1, 1, centerAnchored: true);
        _crosshair.AddChild(center);

        BuildCaptureOverlay();
    }

    private void BuildCaptureOverlay()
    {
        if (_presentationRoot == null) return;
        var shade = new ColorRect
        {
            Color = new Color(0.003f, 0.008f, 0.014f, 0.94f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        _presentationRoot.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _captureOverlay = shade;

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var column = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(480, 0),
            Alignment = BoxContainer.AlignmentMode.Center
        };
        column.AddThemeConstantOverride("separation", 12);
        center.AddChild(column);
        _captureKicker = new Label
        {
            Text = "RINGWORLD / FIELD NOTE 01",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _captureKicker.AddThemeFontSizeOverride("font_size", 13);
        _captureKicker.AddThemeColorOverride("font_color", RingUiStyle.Accent);
        column.AddChild(_captureKicker);
        _captureTitle = new Label
        {
            Text = "A WORLD BENT INTO A RING",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _captureTitle.AddThemeFontSizeOverride("font_size", 38);
        _captureTitle.AddThemeColorOverride("font_color", RingUiStyle.AccentBright);
        column.AddChild(_captureTitle);
        var rule = new ColorRect
        {
            Color = RingUiStyle.Accent,
            CustomMinimumSize = new Vector2(120, 2),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        column.AddChild(rule);
        _captureSubtitle = new Label
        {
            Text = "80 KM RADIUS  •  CONTINUOUS VOXEL TERRAIN",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _captureSubtitle.AddThemeFontSizeOverride("font_size", 14);
        _captureSubtitle.Modulate = RingUiStyle.Muted;
        column.AddChild(_captureSubtitle);
    }

    private void ConfigureCapturePresentation()
    {
        string topic = string.Empty;
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--shorts-topic=", StringComparison.Ordinal))
                topic = argument["--shorts-topic=".Length..].Trim().ToLowerInvariant();
            else if (argument.StartsWith("--shorts-preroll=", StringComparison.Ordinal)
                && double.TryParse(argument["--shorts-preroll=".Length..], out double preroll))
                _capturePrerollSeconds = Math.Clamp(preroll, 0.0, 30.0);
        }
        if (string.IsNullOrEmpty(topic)) return;

        _captureActive = true;
        if (_brandCard != null) _brandCard.Visible = false;
        if (_statusCard != null) _statusCard.Visible = false;
        if (_hintCard != null) _hintCard.Visible = false;
        if (_crosshair != null) _crosshair.Visible = false;
        if (_captureOverlay != null) _captureOverlay.Visible = _capturePrerollSeconds <= 0.0;
        SetCaptureCopy(topic);

        if (GetNodeOrNull<CanvasLayer>("../TerrainMinimap") is { } minimap)
            minimap.Visible = false;
    }

    private void SetCaptureCopy(string topic)
    {
        if (_captureKicker == null || _captureTitle == null || _captureSubtitle == null) return;
        (_captureKicker.Text, _captureTitle.Text, _captureSubtitle.Text) = topic switch
        {
            "clouds" => ("HALO PLAYERS / FIELD NOTE 02", "THE SKY IS PART OF THE RING", "ULTRA CLOUDS  •  SEAMLESS STORMS  •  INNER-SURFACE WEATHER"),
            "voxels" => ("HALO PLAYERS / FIELD NOTE 03", "THIS HALO-SCALE RING IS EDITABLE", "1 METER VOXELS  •  5,000 KM RADIUS"),
            _ => ("HALO PLAYERS / FIELD NOTE 01", "WHAT STANDING ON A HALO RING FEELS LIKE", "5,000 KM RADIUS  •  318 KM WIDE  •  FULL-SCALE HABITAT")
        };
    }

    private void UpdateCaptureIntro(double delta)
    {
        _captureElapsed += delta;
        if (_captureOverlay == null) return;
        double introElapsed = _captureElapsed - _capturePrerollSeconds;
        if (introElapsed < 0.0)
        {
            _captureOverlay.Visible = false;
            return;
        }
        if (introElapsed >= CaptureIntroSeconds)
        {
            _captureOverlay.Visible = false;
            return;
        }
        _captureOverlay.Visible = true;
        float alpha = introElapsed switch
        {
            < 0.25 => (float)(introElapsed / 0.25),
            < 2.75 => 1.0f,
            < CaptureIntroSeconds => (float)((CaptureIntroSeconds - introElapsed) / (CaptureIntroSeconds - 2.75)),
            _ => 0.0f
        };
        _captureOverlay.Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha, 0, 1));
    }

    private void HideLegacyPresentationUi()
    {
        if (GetNodeOrNull<Node>("../Instructions") is CanvasItem instructions)
            instructions.Visible = false;
        if (GetNodeOrNull<CanvasLayer>("../LodDebugHud") is { } diagnostics)
            diagnostics.Visible = false;
    }

    private void RefreshStatus()
    {
        if (_explorer == null || _grid?.TerrainSampler == null) return;
        var canonical = _explorer.CanonicalPosition;
        long around = (long)Math.Round(canonical.AroundMeters);
        int across = Mathf.Clamp(Mathf.RoundToInt((float)canonical.AcrossMeters), 0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        var edits = _grid.CaptureLodSurfaceEditSnapshot();
        float surface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
            ? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
            : _grid.TerrainSampler.GetSurfaceHeight(around, across);
        double altitude = canonical.VerticalMeters - surface;
        float speed = _explorer.NoclipEnabled ? _explorer.NoclipCurrentSpeedMetersPerSecond : _explorer.Velocity.Length();
        if (_modeLabel != null)
            _modeLabel.Text = _explorer.NoclipEnabled ? "FLIGHT MODE  /  UNRESTRICTED" : "SURFACE MODE  /  GRAVITY LOCKED";
        if (_coordinateLabel != null)
            _coordinateLabel.Text = $"ARC {FormatDistance(canonical.AroundMeters)}  •  ACROSS {FormatDistance(canonical.AcrossMeters)}";
        if (_altitudeLabel != null)
            _altitudeLabel.Text = $"ALT {FormatDistance(altitude)}  •  SPEED {FormatSpeed(speed)}";
    }

    private static string FormatDistance(double meters)
    {
        double absolute = Math.Abs(meters);
        return absolute >= 1000.0 ? $"{meters / 1000.0:0.0} KM" : $"{meters:0} M";
    }

    private static string FormatSpeed(float metersPerSecond) => metersPerSecond >= 1000.0f
        ? $"{metersPerSecond / 1000.0f:0.0} KM/S"
        : $"{metersPerSecond:0} M/S";

    private static PanelContainer CreatePanel(Control parent, float left, float top, float right, float bottom, bool bottomAnchored = false, bool centerAnchored = false)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel());
        parent.AddChild(panel);
        if (centerAnchored)
        {
            panel.AnchorLeft = 0.5f;
            panel.AnchorRight = 0.5f;
        }
        if (bottomAnchored)
        {
            panel.AnchorTop = 1.0f;
            panel.AnchorBottom = 1.0f;
        }
        panel.OffsetLeft = left;
        panel.OffsetTop = top;
        panel.OffsetRight = right;
        panel.OffsetBottom = bottom;
        return panel;
    }

    private static MarginContainer CreateMargin(Control parent, int left, int top, int right, int bottom)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", left);
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddThemeConstantOverride("margin_right", right);
        margin.AddThemeConstantOverride("margin_bottom", bottom);
        parent.AddChild(margin);
        return margin;
    }

    private void AddCrosshairLine(float left, float top, float right, float bottom)
    {
        if (_crosshair == null) return;
        _crosshair.AddChild(MakeRect(new Color(RingUiStyle.Accent.R, RingUiStyle.Accent.G, RingUiStyle.Accent.B, 0.82f), left, top, right, bottom, centerAnchored: true));
    }

    private static ColorRect MakeRect(Color color, float left, float top, float right, float bottom, bool centerAnchored)
    {
        var rect = new ColorRect { Color = color, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (centerAnchored)
        {
            rect.AnchorLeft = 0.5f;
            rect.AnchorRight = 0.5f;
            rect.AnchorTop = 0.5f;
            rect.AnchorBottom = 0.5f;
        }
        rect.OffsetLeft = left;
        rect.OffsetTop = top;
        rect.OffsetRight = right;
        rect.OffsetBottom = bottom;
        return rect;
    }
}
