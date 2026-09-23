using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayMotionDefaults _motionDefaults = null!;
    private CheckButton? _physicsInterpolationToggle;
    private HSlider? _walkableSlopeSlider;
    private Label? _walkableSlopeValue;
    private Label? _motionSaveStatus;
    private double _motionSaveDelay;
    private double _motionSaveRetryDelay;
    private string? _motionDefaultsPathOverride;

    private void InitializeMotionTuning()
    {
        // SceneTree interpolation is global. Keep this gameplay root opted out
        // so only children that explicitly opt in (the Explorer) are smoothed.
        Node? gameplayRoot = GetParent();
        if (gameplayRoot != null)
            gameplayRoot.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
        _motionDefaults = new GameplayMotionDefaults(_motionDefaultsPathOverride);
        _motionDefaults.Initialize(GetTree(), Explorer);
        ResetExplorerPhysicsInterpolationIfEnabled();
    }

    private void ProcessMotionTuning(double delta)
    {
        if (!_motionDefaults.Dirty) return;
        _motionSaveDelay -= delta;
        _motionSaveRetryDelay -= delta;
        if (_motionSaveDelay <= 0 && _motionSaveRetryDelay <= 0 && !FlushMotionDefaults())
            _motionSaveRetryDelay = 5.0;
    }

    private void BuildMotionTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Motion");
        VBoxContainer section = TuningMenuUi.CreateSection(tab, "First-person motion", "Player camera smoothing");
        _physicsInterpolationToggle = new CheckButton
        {
            Name = "PhysicsInterpolationEnabled",
            Text = "Smooth player motion between physics ticks",
            TooltipText = "Interpolates the player body for rendered frames. Terrain and world systems keep their existing update behavior."
        };
        _physicsInterpolationToggle.Toggled += SetPhysicsInterpolation;
        section.AddChild(_physicsInterpolationToggle);
        section.AddChild(new Label
        {
            Text = "Use this to smooth visible 60 Hz movement stepping at higher render frame rates. Mouse look remains immediate.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        });
        section.AddChild(new Label { Text = "Maximum walkable slope" });
        var slopeRow = new HBoxContainer();
        _walkableSlopeSlider = new HSlider
        {
            Name = "MaxWalkableSlopeDegrees",
            MinValue = GameplayMotionDefaults.MinimumWalkableSlopeDegrees,
            MaxValue = GameplayMotionDefaults.MaximumWalkableSlopeDegrees,
            Step = 1,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Slopes steeper than this stop the player. The Certified opposite-side terrain traversal requires 55 degrees."
        };
        _walkableSlopeSlider.ValueChanged += degrees => SetMaxWalkableSlopeDegrees((float)degrees);
        slopeRow.AddChild(_walkableSlopeSlider);
        _walkableSlopeValue = new Label { CustomMinimumSize = new Vector2(42, 0) };
        slopeRow.AddChild(_walkableSlopeValue);
        section.AddChild(slopeRow);
        var row = new HBoxContainer();
        var save = new Button { Name = "MotionSaveNow", Text = "Save motion setting", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushMotionDefaults();
        row.AddChild(save);
        var reset = new Button { Name = "MotionResetDefaults", Text = "Reset motion", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetMotionSettings;
        row.AddChild(reset);
        tab.AddChild(row);
        _motionSaveStatus = new Label { Name = "MotionDefaultsStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_motionSaveStatus);
        RefreshMotionControls();
    }

    private void SetPhysicsInterpolation(bool enabled)
    {
        if (!_motionDefaults.SetPhysicsInterpolation(GetTree(), enabled)) return;
        ResetExplorerPhysicsInterpolationIfEnabled();
        _motionSaveDelay = .5;
        _motionSaveRetryDelay = 0;
        RefreshMotionControls();
    }

    private void SetMaxWalkableSlopeDegrees(float degrees)
    {
        if (!_motionDefaults.SetMaxWalkableSlopeDegrees(Explorer, degrees)) return;
        _motionSaveDelay = .5;
        _motionSaveRetryDelay = 0;
        RefreshMotionControls();
    }

    private void ResetMotionSettings()
    {
        _motionDefaults.Reset(GetTree(), Explorer);
        ResetExplorerPhysicsInterpolationIfEnabled();
        _motionSaveDelay = 0;
        _motionSaveRetryDelay = 0;
        FlushMotionDefaults();
        RefreshMotionControls();
    }

    private bool FlushMotionDefaults()
    {
        bool saved = _motionDefaults.Save();
        RefreshMotionControls();
        return saved;
    }

    private void RefreshMotionControls()
    {
        if (_physicsInterpolationToggle != null && _physicsInterpolationToggle.ButtonPressed != _motionDefaults.Settings.PhysicsInterpolationEnabled)
            _physicsInterpolationToggle.SetPressedNoSignal(_motionDefaults.Settings.PhysicsInterpolationEnabled);
        if (_walkableSlopeSlider != null)
            _walkableSlopeSlider.SetValueNoSignal(_motionDefaults.Settings.MaxWalkableSlopeDegrees);
        if (_walkableSlopeValue != null)
            _walkableSlopeValue.Text = $"{_motionDefaults.Settings.MaxWalkableSlopeDegrees:0}°";
        if (_motionSaveStatus != null)
            _motionSaveStatus.Text = _motionDefaults.Dirty
                ? "Motion setting pending save."
                : _motionDefaults.LastError.Length > 0 ? $"Motion save error: {_motionDefaults.LastError}" : "Motion setting saved.";
    }

    private void ResetExplorerPhysicsInterpolationIfEnabled()
    {
        if (!GetTree().PhysicsInterpolation) return;
        GetNodeOrNull<Node3D>("../Explorer")?.ResetPhysicsInterpolation();
    }

    private Node? Explorer => GetNodeOrNull<Node>("../Explorer");

    public Godot.Collections.Dictionary GetMotionTuningDiagnostics() => new()
    {
        ["path"] = _motionDefaults.GetPath(),
        ["dirty"] = _motionDefaults.Dirty,
        ["last_error"] = _motionDefaults.LastError,
        ["physics_interpolation_enabled"] = _motionDefaults.Settings.PhysicsInterpolationEnabled,
        ["max_walkable_slope_degrees"] = _motionDefaults.Settings.MaxWalkableSlopeDegrees,
        ["runtime_physics_interpolation"] = GetTree().PhysicsInterpolation
    };

    /// <summary>Test fixture hook that keeps motion preferences outside the player's user:// defaults.</summary>
    public void InstallMotionDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", System.StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", System.StringComparison.Ordinal)) return;
        _motionDefaultsPathOverride = isolatedPath;
        if (_motionDefaults is null || !_motionDefaults.Initialized) return;
        _motionDefaults = new GameplayMotionDefaults(isolatedPath);
        _motionDefaults.Initialize(GetTree(), Explorer);
        ResetExplorerPhysicsInterpolationIfEnabled();
        RefreshMotionControls();
    }
}
