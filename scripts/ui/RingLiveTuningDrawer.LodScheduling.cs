using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayLodSchedulingDefaults? _lodSchedulingDefaults;
    private HSlider? _lodBuildStarts, _lodBuildStartBudget, _lodBuildStartStarvation;
    private Label? _lodSchedulingStatus;
    private double _lodSchedulingSaveDelay, _lodSchedulingSaveRetry;

    private void InitializeLodSchedulingTuning()
    {
        _lodSchedulingDefaults = new GameplayLodSchedulingDefaults();
        _lodSchedulingDefaults.Load();
        // Without a saved choice the coordinator keeps its own defaults.
        if (_coordinator != null && _lodSchedulingDefaults.HasSavedFile) _lodSchedulingDefaults.Apply(_coordinator);
    }

    private void ProcessLodSchedulingTuning(double delta)
    {
        if (_lodSchedulingDefaults?.Dirty != true) return;
        _lodSchedulingSaveDelay -= delta;
        _lodSchedulingSaveRetry -= delta;
        if (_lodSchedulingSaveDelay <= 0 && _lodSchedulingSaveRetry <= 0 && !FlushLodSchedulingDefaults()) _lodSchedulingSaveRetry = 5;
    }

    public bool FlushLodSchedulingDefaults()
    {
        bool saved = _lodSchedulingDefaults?.Save() ?? true;
        RefreshLodSchedulingStatus();
        return saved;
    }

    private void BuildLodSchedulingSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "LOD build starts",
            "Starting a terrain LOD build costs main-thread time. These limit how many builds begin per frame so loading does not hitch; a level that keeps losing to finer ones is promoted after the starvation window.");
        _lodBuildStarts = AddLodSchedulingSlider(section, "LodBuildStartsPerFrame", "Build starts per frame",
            GameplayLodSchedulingDefaults.MinimumStarts, GameplayLodSchedulingDefaults.MaximumStarts, 1,
            _coordinator?.MaxBuildStartsPerFrame ?? 1,
            "Maximum LOD builds begun in one frame. Applies immediately.");
        _lodBuildStartBudget = AddLodSchedulingSlider(section, "LodBuildStartBudget", "Build start budget (ms)",
            GameplayLodSchedulingDefaults.MinimumBudgetMilliseconds, GameplayLodSchedulingDefaults.MaximumBudgetMilliseconds, .25,
            _coordinator?.MaxBuildStartMillisecondsPerFrame ?? 2.0,
            "Main-thread time per frame for beginning builds. The first start each frame is never blocked.");
        _lodBuildStartStarvation = AddLodSchedulingSlider(section, "LodBuildStartStarvationFrames", "Starvation frames",
            GameplayLodSchedulingDefaults.MinimumStarvationFrames, GameplayLodSchedulingDefaults.MaximumStarvationFrames, 1,
            _coordinator?.BuildStartStarvationFrames ?? 30,
            "Frames a level may wait before it is started ahead of finer levels.");
        var row = new HBoxContainer();
        var save = new Button { Name = "LodSchedulingSave", Text = "Save LOD scheduling", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => { QueueLodScheduling(); FlushLodSchedulingDefaults(); };
        row.AddChild(save);
        var reset = new Button { Name = "LodSchedulingReset", Text = "Reset LOD scheduling", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () =>
        {
            _lodSchedulingDefaults?.Reset();
            if (_coordinator != null && _lodSchedulingDefaults != null) _lodSchedulingDefaults.Apply(_coordinator);
            RefreshLodSchedulingControls();
            FlushLodSchedulingDefaults();
        };
        row.AddChild(reset);
        section.AddChild(row);
        _lodSchedulingStatus = new Label { Name = "LodSchedulingStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_lodSchedulingStatus);
        RefreshLodSchedulingStatus();
    }

    private HSlider AddLodSchedulingSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _coordinator != null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueLodScheduling(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueLodScheduling()
    {
        if (_lodBuildStarts == null || _lodBuildStartBudget == null || _lodBuildStartStarvation == null || _lodSchedulingDefaults == null) return;
        bool changed = _lodSchedulingDefaults.Set((int)_lodBuildStarts.Value, (float)_lodBuildStartBudget.Value, (int)_lodBuildStartStarvation.Value);
        if (_coordinator != null) _lodSchedulingDefaults.Apply(_coordinator);
        if (!changed) return;
        _lodSchedulingSaveDelay = .75;
        RefreshLodSchedulingStatus();
    }

    private void RefreshLodSchedulingControls()
    {
        if (_lodSchedulingDefaults == null) return;
        SetLodSchedulingSliderNoSignal(_lodBuildStarts, _lodSchedulingDefaults.Settings.MaxBuildStartsPerFrame);
        SetLodSchedulingSliderNoSignal(_lodBuildStartBudget, _lodSchedulingDefaults.Settings.BuildStartBudgetMilliseconds);
        SetLodSchedulingSliderNoSignal(_lodBuildStartStarvation, _lodSchedulingDefaults.Settings.StarvationFrames);
        RefreshLodSchedulingStatus();
    }

    private static void SetLodSchedulingSliderNoSignal(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent().GetChild(2) is Label number) number.Text = slider.Value.ToString("0.##");
    }

    private void RefreshLodSchedulingStatus()
    {
        if (_lodSchedulingStatus == null || _lodSchedulingDefaults == null) return;
        _lodSchedulingStatus.Text = _coordinator == null ? "The terrain LOD coordinator is unavailable in this scene."
            : _lodSchedulingDefaults.LastError.Length > 0 ? "Save error: " + _lodSchedulingDefaults.LastError
            : _lodSchedulingDefaults.Dirty ? "LOD scheduling pending save." : "LOD scheduling saved.";
    }
}
