using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    [Export] public NodePath MinimapPath { get; set; } = new("../TerrainMinimap");

    private GameplayMinimapDefaults? _minimapDefaults;
    private HSlider? _minimapRenderRate;
    private Label? _minimapStatus;
    private double _minimapSaveDelay, _minimapSaveRetry;

    private RingTerrainMinimap? ResolveMinimap() => GetNodeOrNull<RingTerrainMinimap>(MinimapPath);

    private void InitializeMinimapTuning()
    {
        _minimapDefaults = new();
        _minimapDefaults.Load();
        ApplyMinimapSettings();
    }

    private void ApplyMinimapSettings()
    {
        if (_minimapDefaults == null) return;
        RingTerrainMinimap? minimap = ResolveMinimap();
        if (minimap != null) minimap.RenderFramesPerSecond = _minimapDefaults.Settings.RenderFramesPerSecond;
    }

    private void ProcessMinimapTuning(double delta)
    {
        if (_minimapDefaults?.Dirty != true) return;
        _minimapSaveDelay -= delta;
        _minimapSaveRetry -= delta;
        if (_minimapSaveDelay <= 0 && _minimapSaveRetry <= 0 && !FlushMinimapDefaults()) _minimapSaveRetry = 5;
    }

    public bool FlushMinimapDefaults()
    {
        bool saved = _minimapDefaults?.Save() ?? true;
        RefreshMinimapStatus();
        return saved;
    }

    private void BuildMinimapTab(TabContainer tabs)
    {
        var tab = CreateTab(tabs, "Radar");
        var section = TuningMenuUi.CreateSection(tab, "Radar refresh", "How often the terrain radar redraws. Lower rates save GPU time; the radar stops rendering entirely while hidden.");
        float initial = _minimapDefaults?.Settings.RenderFramesPerSecond ?? 15f;
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Radar refresh rate (fps)", CustomMinimumSize = new Vector2(160, 0) });
        _minimapRenderRate = new HSlider { Name = "MinimapRenderRate", MinValue = 5, MaxValue = 60, Step = 1, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = initial.ToString("0"), CustomMinimumSize = new Vector2(55, 0) };
        _minimapRenderRate.ValueChanged += value => { number.Text = value.ToString("0"); QueueMinimapSettings(); };
        row.AddChild(_minimapRenderRate);
        row.AddChild(number);
        section.AddChild(row);
        var buttons = new HBoxContainer();
        var save = new Button { Name = "MinimapSave", Text = "Save radar settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushMinimapDefaults();
        buttons.AddChild(save);
        var reset = new Button { Name = "MinimapReset", Text = "Reset radar settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () => { _minimapDefaults?.Reset(); ApplyMinimapSettings(); RefreshMinimapControls(); FlushMinimapDefaults(); };
        buttons.AddChild(reset);
        tab.AddChild(buttons);
        _minimapStatus = new Label { Name = "MinimapStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_minimapStatus);
        RefreshMinimapControls();
    }

    private void QueueMinimapSettings()
    {
        if (_minimapRenderRate == null) return;
        if (_minimapDefaults?.Set((float)_minimapRenderRate.Value) != true) return;
        ApplyMinimapSettings();
        _minimapSaveDelay = .75;
        RefreshMinimapStatus();
    }

    private void RefreshMinimapControls()
    {
        if (_minimapDefaults == null) return;
        if (_minimapRenderRate != null)
        {
            _minimapRenderRate.SetValueNoSignal(_minimapDefaults.Settings.RenderFramesPerSecond);
            if (_minimapRenderRate.GetParent().GetChild(2) is Label number) number.Text = _minimapRenderRate.Value.ToString("0");
        }
        RefreshMinimapStatus();
    }

    private void RefreshMinimapStatus()
    {
        if (_minimapStatus == null || _minimapDefaults == null) return;
        _minimapStatus.Text = _minimapDefaults.LastError.Length > 0 ? "Save error: " + _minimapDefaults.LastError
            : _minimapDefaults.Dirty ? "Radar settings pending save." : "Radar settings saved.";
    }
}
