using System;
using Godot;
using RingworldTechDemo.ContainmentField;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private RingContainmentField? _containmentField;
    private GameplayContainmentFieldDefaults? _containmentFieldDefaults;
    private string? _containmentFieldDefaultsPathOverride;
    private HSlider? _containmentShimmerStrength, _containmentShimmerRange, _containmentSheenStrength, _containmentLatticeStrength;
    private Label? _containmentFieldStatus;
    private double _containmentFieldSaveDelay, _containmentFieldSaveRetry, _containmentFieldBindDelay;

    private void InitializeContainmentFieldTuning()
    {
        _containmentField = GetNodeOrNull<RingContainmentField>("../ContainmentField");
        _containmentFieldDefaults = new GameplayContainmentFieldDefaults(_containmentFieldDefaultsPathOverride);
        _containmentFieldDefaults.Load();
        if (_containmentFieldDefaults.HasSavedFile) _containmentFieldDefaults.Apply(_containmentField);
    }

    private void ProcessContainmentFieldTuning(double delta)
    {
        if (_containmentField == null && (_containmentFieldBindDelay -= delta) <= 0)
        {
            _containmentFieldBindDelay = .25;
            _containmentField = GetNodeOrNull<RingContainmentField>("../ContainmentField");
            if (_containmentField != null)
            {
                if (_containmentFieldDefaults?.HasSavedFile == true) _containmentFieldDefaults.Apply(_containmentField);
                RefreshContainmentFieldControls();
            }
        }
        if (_containmentFieldDefaults?.Dirty != true) return;
        _containmentFieldSaveDelay -= delta;
        _containmentFieldSaveRetry -= delta;
        if (_containmentFieldSaveDelay <= 0 && _containmentFieldSaveRetry <= 0 && !FlushContainmentFieldDefaults()) _containmentFieldSaveRetry = 5;
    }

    public bool FlushContainmentFieldDefaults()
    {
        bool saved = _containmentFieldDefaults?.Save() ?? true;
        RefreshContainmentFieldStatus();
        return saved;
    }

    public void InstallContainmentFieldDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _containmentFieldDefaultsPathOverride = isolatedPath;
        if (_containmentFieldDefaults is null) return;
        _containmentFieldDefaults = new GameplayContainmentFieldDefaults(isolatedPath);
        _containmentFieldDefaults.Load();
        if (_containmentFieldDefaults.HasSavedFile) _containmentFieldDefaults.Apply(_containmentField);
        RefreshContainmentFieldControls();
    }

    private void BuildContainmentFieldSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Containment field",
            "The faint shimmer of the field wall seen from close up. Far views are not affected.");
        var s = _containmentFieldDefaults?.Settings ?? new GameplayContainmentFieldSettings();
        _containmentShimmerStrength = AddContainmentFieldSlider(section, "ContainmentShimmerStrength", "Shimmer strength", 0, 2, .05, s.ShimmerStrength,
            "Refraction of whatever is behind the containment field, close up (within the shimmer range). 0 with sheen 0 hides the near field.");
        _containmentShimmerRange = AddContainmentFieldSlider(section, "ContainmentShimmerRangeKm", "Shimmer range (km)", .5, 10, .1, s.ShimmerRangeKm,
            "Distance from the field inside which the close-up shimmer and sheen show.");
        _containmentSheenStrength = AddContainmentFieldSlider(section, "ContainmentSheenStrength", "Field sheen", 0, 2, .05, s.SheenStrength,
            "Soft sheen of the field: a Fresnel-weighted reflection of the sky, the habitat below and the sun, near zero head-on. Lit by the real light: dark at night.");
        _containmentLatticeStrength = AddContainmentFieldSlider(section, "ContainmentLatticeStrength", "Lattice seams", 0, 2, .05, s.LatticeStrength,
            "The 100 m honeycomb seams: clear lens-like beads that bend what is behind them, catch the sky and sun, and carry a faint flowing cyan energy (dimmer at night). Ripples and strikes energise them.");
        var reset = new Button { Name = "ContainmentFieldReset", Text = "Reset containment field", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Restores the containment field settings to their defaults." };
        reset.Pressed += ResetContainmentField;
        section.AddChild(reset);
        _containmentFieldStatus = new Label { Name = "ContainmentFieldStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_containmentFieldStatus);
        RefreshContainmentFieldStatus();
    }

    private HSlider AddContainmentFieldSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueContainmentFieldSettings(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueContainmentFieldSettings()
    {
        if (_containmentFieldDefaults == null || _containmentShimmerStrength == null || _containmentShimmerRange == null || _containmentSheenStrength == null || _containmentLatticeStrength == null) return;
        bool changed = _containmentFieldDefaults.SetShimmerStrength((float)_containmentShimmerStrength.Value);
        changed |= _containmentFieldDefaults.SetShimmerRangeKm((float)_containmentShimmerRange.Value);
        changed |= _containmentFieldDefaults.SetSheenStrength((float)_containmentSheenStrength.Value);
        changed |= _containmentFieldDefaults.SetLatticeStrength((float)_containmentLatticeStrength.Value);
        _containmentFieldDefaults.Apply(_containmentField);
        if (!changed) return;
        _containmentFieldSaveDelay = .75;
        RefreshContainmentFieldStatus();
    }

    private void ResetContainmentField()
    {
        _containmentFieldDefaults?.Reset();
        _containmentFieldDefaults?.Apply(_containmentField);
        RefreshContainmentFieldControls();
        FlushContainmentFieldDefaults();
    }

    private void RefreshContainmentFieldControls()
    {
        if (_containmentFieldDefaults == null) return;
        var s = _containmentFieldDefaults.Settings;
        SetEmitterSliderNoSignal(_containmentShimmerStrength, s.ShimmerStrength);
        SetEmitterSliderNoSignal(_containmentShimmerRange, s.ShimmerRangeKm);
        SetEmitterSliderNoSignal(_containmentSheenStrength, s.SheenStrength);
        SetEmitterSliderNoSignal(_containmentLatticeStrength, s.LatticeStrength);
        RefreshContainmentFieldStatus();
    }

    private void RefreshContainmentFieldStatus()
    {
        if (_containmentFieldStatus == null || _containmentFieldDefaults == null) return;
        _containmentFieldStatus.Text = _containmentFieldDefaults.LastError.Length > 0 ? "Save error: " + _containmentFieldDefaults.LastError
            : _containmentFieldDefaults.Dirty ? "Containment field settings pending save." : "Containment field settings saved.";
    }
}
