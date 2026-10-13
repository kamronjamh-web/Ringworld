using Godot;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private SmoothTerrainGrid? _streamingGrid;
    private GameplayTerrainStreamingDefaults? _streamingDefaults;
    private HSlider? _streamingWorkers, _streamingInstalls, _streamingBudget;
    private Label? _streamingStatus;
    private double _streamingSaveDelay, _streamingSaveRetry;

    private void InitializeTerrainStreamingTuning()
    {
        _streamingGrid = GetNodeOrNull<SmoothTerrainGrid>("../SmoothTerrainGrid");
        _streamingDefaults = new GameplayTerrainStreamingDefaults();
        _streamingDefaults.Load();
        // Without a saved choice the grid keeps its own (hardware-derived) defaults.
        if (_streamingGrid != null && _streamingDefaults.HasSavedFile) _streamingDefaults.Apply(_streamingGrid);
    }

    private void ProcessTerrainStreamingTuning(double delta)
    {
        if (_streamingDefaults?.Dirty != true) return;
        _streamingSaveDelay -= delta;
        _streamingSaveRetry -= delta;
        if (_streamingSaveDelay <= 0 && _streamingSaveRetry <= 0 && !FlushTerrainStreamingDefaults()) _streamingSaveRetry = 5;
    }

    public bool FlushTerrainStreamingDefaults()
    {
        bool saved = _streamingDefaults?.Save() ?? true;
        RefreshTerrainStreamingStatus();
        return saved;
    }

    private void BuildTerrainStreamingSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Terrain streaming",
            "Throughput of background terrain and cave generation. More workers finish loading sooner but take CPU from the game thread; the install budget caps main-thread time spent adding finished meshes each frame.");
        _streamingWorkers = AddStreamingSlider(section, "TerrainStreamingWorkers", "Worker threads",
            GameplayTerrainStreamingDefaults.MinimumWorkers, GameplayTerrainStreamingDefaults.MaximumWorkers, 1,
            _streamingGrid?.MaxWorkers ?? SmoothTerrainGrid.DefaultMaxWorkers,
            "Raising applies immediately. Lowering takes effect as running jobs finish.");
        _streamingInstalls = AddStreamingSlider(section, "TerrainStreamingMaxInstalls", "Installs per frame (cap)",
            GameplayTerrainStreamingDefaults.MinimumInstalls, GameplayTerrainStreamingDefaults.MaximumInstalls, 1,
            _streamingGrid?.MaxInstallsPerFrame ?? 8,
            "Safety cap across all tiers. Empty or uniform results do not count. Applies immediately.");
        _streamingBudget = AddStreamingSlider(section, "TerrainStreamingInstallBudget", "Install budget (ms)",
            GameplayTerrainStreamingDefaults.MinimumBudgetMilliseconds, GameplayTerrainStreamingDefaults.MaximumBudgetMilliseconds, .25,
            _streamingGrid?.InstallBudgetMilliseconds ?? 1.5,
            "Main-thread time per frame for installing finished meshes. Applies immediately.");
        var row = new HBoxContainer();
        var save = new Button { Name = "TerrainStreamingSave", Text = "Save streaming settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => { QueueTerrainStreaming(); FlushTerrainStreamingDefaults(); };
        row.AddChild(save);
        var reset = new Button { Name = "TerrainStreamingReset", Text = "Reset streaming settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () =>
        {
            _streamingDefaults?.Reset();
            if (_streamingGrid != null && _streamingDefaults != null) _streamingDefaults.Apply(_streamingGrid);
            RefreshTerrainStreamingControls();
            FlushTerrainStreamingDefaults();
        };
        row.AddChild(reset);
        section.AddChild(row);
        _streamingStatus = new Label { Name = "TerrainStreamingStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_streamingStatus);
        RefreshTerrainStreamingStatus();
    }

    private HSlider AddStreamingSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _streamingGrid != null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueTerrainStreaming(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueTerrainStreaming()
    {
        if (_streamingWorkers == null || _streamingInstalls == null || _streamingBudget == null || _streamingDefaults == null) return;
        bool changed = _streamingDefaults.Set((int)_streamingWorkers.Value, (int)_streamingInstalls.Value, (float)_streamingBudget.Value);
        if (_streamingGrid != null) _streamingDefaults.Apply(_streamingGrid);
        if (!changed) return;
        _streamingSaveDelay = .75;
        RefreshTerrainStreamingStatus();
    }

    private void RefreshTerrainStreamingControls()
    {
        if (_streamingDefaults == null) return;
        SetStreamingSliderNoSignal(_streamingWorkers, _streamingDefaults.Settings.MaxWorkers);
        SetStreamingSliderNoSignal(_streamingInstalls, _streamingDefaults.Settings.MaxInstallsPerFrame);
        SetStreamingSliderNoSignal(_streamingBudget, _streamingDefaults.Settings.InstallBudgetMilliseconds);
        RefreshTerrainStreamingStatus();
    }

    private static void SetStreamingSliderNoSignal(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent().GetChild(2) is Label number) number.Text = slider.Value.ToString("0.##");
    }

    private void RefreshTerrainStreamingStatus()
    {
        if (_streamingStatus == null || _streamingDefaults == null) return;
        _streamingStatus.Text = _streamingGrid == null ? "Smooth terrain streaming is unavailable in this scene."
            : _streamingDefaults.LastError.Length > 0 ? "Save error: " + _streamingDefaults.LastError
            : _streamingDefaults.Dirty ? "Streaming settings pending save." : "Streaming settings saved.";
    }
}
