using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayAntiAliasingDefaults _antiAliasingDefaults = null!;
    private OptionButton? _antiAliasingOptions;
    private Label? _antiAliasingStatus;
    private Label? _antiAliasingSaveStatus;
    private double _antiAliasingSaveDelay;
    private double _antiAliasingSaveRetryDelay;
    private double _antiAliasingRefreshDelay;
    private string? _antiAliasingDefaultsPathOverride;

    private void InitializeAntiAliasingTuning()
    {
        _antiAliasingDefaults = new GameplayAntiAliasingDefaults(_antiAliasingDefaultsPathOverride);
        _antiAliasingDefaults.Initialize(MainViewport);
    }

    private void ProcessAntiAliasingTuning(double delta)
    {
        if (_antiAliasingDefaults.Dirty)
        {
            _antiAliasingSaveDelay -= delta;
            _antiAliasingSaveRetryDelay -= delta;
            if (_antiAliasingSaveDelay <= 0 && _antiAliasingSaveRetryDelay <= 0 && !FlushAntiAliasingDefaults())
                _antiAliasingSaveRetryDelay = 5.0;
        }
        if (!_open) return;
        _antiAliasingRefreshDelay -= delta;
        if (_antiAliasingRefreshDelay <= 0)
        {
            _antiAliasingRefreshDelay = .25;
            RefreshAntiAliasingControls();
        }
    }

    private void BuildAntiAliasingTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Anti-aliasing");
        VBoxContainer modeSection = TuningMenuUi.CreateSection(tab, "Mode", "Gameplay anti-aliasing");
        _antiAliasingOptions = new OptionButton { Name = "AntiAliasingMode", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (AntiAliasingMode mode in System.Enum.GetValues<AntiAliasingMode>())
            _antiAliasingOptions.AddItem(GameplayAntiAliasingDefaults.GetModeName(mode), (int)mode);
        _antiAliasingOptions.ItemSelected += index => SetAntiAliasingMode((AntiAliasingMode)_antiAliasingOptions.GetItemId((int)index));
        modeSection.AddChild(_antiAliasingOptions);
        _antiAliasingStatus = new Label { Name = "AntiAliasingRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        modeSection.AddChild(_antiAliasingStatus);
        var help = new Label
        {
            Text = "FSR 2 uses its own temporal AA. Your selection is kept and resumes when Native resolution is selected in the pause menu.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        };
        modeSection.AddChild(help);
        modeSection.AddChild(new Label
        {
            Text = "FXAA/SMAA are low-cost post-process choices. TAA reduces temporal shimmer; MSAA targets geometry edges.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        });
        var row = new HBoxContainer();
        var save = new Button { Name = "AntiAliasingSaveNow", Text = "Save AA setting", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushAntiAliasingDefaults();
        row.AddChild(save);
        var reset = new Button { Name = "AntiAliasingResetDefaults", Text = "Reset AA only", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetAntiAliasingSettings;
        row.AddChild(reset);
        tab.AddChild(row);
        _antiAliasingSaveStatus = new Label { Name = "AntiAliasingDefaultsStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_antiAliasingSaveStatus);
        BuildRenderResolutionControls(tab);
        RefreshAntiAliasingControls();
    }

    private void SetAntiAliasingMode(AntiAliasingMode mode)
    {
        if (!_antiAliasingDefaults.SetMode(MainViewport, mode)) return;
        _antiAliasingSaveDelay = .5;
        _antiAliasingSaveRetryDelay = 0;
        RefreshAntiAliasingControls();
    }

    private void ResetAntiAliasingSettings()
    {
        _antiAliasingDefaults.Reset(MainViewport);
        _antiAliasingSaveDelay = 0;
        _antiAliasingSaveRetryDelay = 0;
        FlushAntiAliasingDefaults();
        RefreshAntiAliasingControls();
    }

    private bool FlushAntiAliasingDefaults()
    {
        bool saved = _antiAliasingDefaults.Save();
        RefreshAntiAliasingControls();
        return saved;
    }

    private void RefreshAntiAliasingControls()
    {
        if (_antiAliasingOptions != null)
        {
            int selected = (int)_antiAliasingDefaults.Settings.Mode;
            if (_antiAliasingOptions.Selected != selected) _antiAliasingOptions.Select(selected);
        }
        if (_antiAliasingStatus != null) _antiAliasingStatus.Text = _antiAliasingDefaults.GetEffectiveStatus(MainViewport);
        if (_antiAliasingSaveStatus != null)
            _antiAliasingSaveStatus.Text = _antiAliasingDefaults.Dirty
                ? "AA setting pending save."
                : _antiAliasingDefaults.LastError.Length > 0 ? $"AA save error: {_antiAliasingDefaults.LastError}" : "AA setting saved.";
        RefreshRenderResolutionControls();
    }

    public Godot.Collections.Dictionary GetAntiAliasingTuningDiagnostics() => new()
    {
        ["path"] = _antiAliasingDefaults.GetPath(),
        ["dirty"] = _antiAliasingDefaults.Dirty,
        ["last_error"] = _antiAliasingDefaults.LastError,
        ["requested_mode"] = GameplayAntiAliasingDefaults.GetModeName(_antiAliasingDefaults.Settings.Mode),
        ["effective_status"] = _antiAliasingDefaults.GetEffectiveStatus(MainViewport)
    };

    /// <summary>Test fixture hook that keeps AA preferences outside the player's user:// defaults.</summary>
    public void InstallAntiAliasingDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", System.StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", System.StringComparison.Ordinal)) return;
        _antiAliasingDefaultsPathOverride = isolatedPath;
        if (_antiAliasingDefaults is null || !_antiAliasingDefaults.Initialized) return;
        _antiAliasingDefaults = new GameplayAntiAliasingDefaults(isolatedPath);
        _antiAliasingDefaults.Initialize(MainViewport);
        RefreshAntiAliasingControls();
    }

    // The drawer can be hosted by a SubViewport in diagnostics scenes. AA is
    // deliberately a gameplay-display choice, so only ever change the root.
    private Viewport MainViewport => GetTree().Root;
}
