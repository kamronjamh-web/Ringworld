using System;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private EmitterGridBeamLayer? _emitterGrid;
    private GameplayEmitterGridDefaults? _emitterGridDefaults;
    private string? _emitterGridDefaultsPathOverride;
    private CheckButton? _emitterGridCore, _emitterGridFar, _emitterGridFollowClouds;
    private HSlider? _emitterGridCoreThreshold, _emitterGridCoreMinPixels, _emitterGridNearMax, _emitterGridHandoffStart, _emitterGridHandoffEnd;
    private Label? _emitterGridLive, _emitterGridStatus;
    private double _emitterGridSaveDelay, _emitterGridSaveRetry, _emitterGridLiveDelay;

    private void InitializeEmitterGridTuning()
    {
        _emitterGrid = GetNodeOrNull<EmitterGridBeamLayer>("../EmitterGrid");
        _emitterGridDefaults = new GameplayEmitterGridDefaults(_emitterGridDefaultsPathOverride);
        _emitterGridDefaults.Load();
        _emitterGridDefaults.Apply(_emitterGrid);
    }

    private void ProcessEmitterGridTuning(double delta)
    {
        if (_open && _emitterGridLive != null && (_emitterGridLiveDelay -= delta) <= 0)
        {
            _emitterGridLiveDelay = .25;
            _emitterGrid ??= GetNodeOrNull<EmitterGridBeamLayer>("../EmitterGrid");
            _emitterGridLive.Text = _emitterGrid?.DescribeStatus() ?? "Emitter relay grid is unavailable in this scene.";
            RefreshEmitterGridEditable();
        }
        if (_emitterGridDefaults?.Dirty != true) return;
        _emitterGridSaveDelay -= delta;
        _emitterGridSaveRetry -= delta;
        if (_emitterGridSaveDelay <= 0 && _emitterGridSaveRetry <= 0 && !FlushEmitterGridDefaults()) _emitterGridSaveRetry = 5;
    }

    public bool FlushEmitterGridDefaults()
    {
        bool saved = _emitterGridDefaults?.Save() ?? true;
        RefreshEmitterGridStatus();
        return saved;
    }

    public void InstallEmitterGridDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _emitterGridDefaultsPathOverride = isolatedPath;
        if (_emitterGridDefaults is null) return;
        _emitterGridDefaults = new GameplayEmitterGridDefaults(isolatedPath);
        _emitterGridDefaults.Load();
        _emitterGridDefaults.Apply(_emitterGrid);
        RefreshEmitterGridControls();
    }

    private void BuildEmitterGridSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Emitter relay grid: clouds and far layer",
            "Clouds dim the relay grid beams behind them unless the lit core writes depth. Far beams are drawn in the far viewport under the far clouds, and the near-to-far handoff band follows the cloud handoff by default.");
        _emitterGridLive = new Label { Name = "EmitterGridLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_emitterGridLive);
        var s = _emitterGridDefaults?.Settings ?? new GameplayEmitterGridSettings();
        _emitterGridCore = AddEmitterGridToggle(section, "EmitterGridCore", "Depth-writing core", s.DepthWriteCore,
            "Lets the lit beam core write depth so clouds in front of a beam hide it and clouds behind it do not dim it.");
        _emitterGridCoreThreshold = AddEmitterGridSlider(section, "EmitterGridCoreThreshold", "Core depth threshold", .05, 2, .01, s.CoreDepthThreshold,
            "Core coverage above which a beam fragment writes depth. Lower writes depth over more of the beam edge.");
        _emitterGridCoreMinPixels = AddEmitterGridSlider(section, "EmitterGridCoreMinPixels", "Core depth min width (px)", 1, 8, .5, s.CoreDepthMinPixels,
            "The depth-writing core is widened to at least this many pixels so thin distant beams still occlude clouds.");
        _emitterGridFar = AddEmitterGridToggle(section, "EmitterGridFar", "Far layer", s.FarLayerEnabled,
            "Draws the whole grid in the far viewport so beams stay visible past the near layer range and under the far clouds.");
        _emitterGridNearMax = AddEmitterGridSlider(section, "EmitterGridNearMax", "Near layer max distance (km)", 100, 850, 5, s.NearLayerMaxKm,
            "Farthest distance the main-camera layer draws beams. The far layer takes over where it fades.");
        _emitterGridFollowClouds = AddEmitterGridToggle(section, "EmitterGridFollowClouds", "Handoff follows clouds", s.FarHandoffFollowsClouds,
            "Uses the cloud handoff distances for the near-to-far beam handoff band. Turn off to set the band by hand.");
        _emitterGridHandoffStart = AddEmitterGridSlider(section, "EmitterGridHandoffStart", "Handoff start (km)", 10, 650, 1, s.FarHandoffStartKm,
            "Manual handoff band start: beams seen against the far backdrop begin moving to the far layer here. Editable only when 'Handoff follows clouds' is off.");
        _emitterGridHandoffEnd = AddEmitterGridSlider(section, "EmitterGridHandoffEnd", "Handoff end (km)", 20, 700, 1, s.FarHandoffEndKm,
            "Manual handoff band end: the far layer owns the beams beyond this distance. Editable only when 'Handoff follows clouds' is off.");
        var reset = new Button { Name = "EmitterGridReset", Text = "Reset emitter relay grid", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Restores the relay grid cloud, depth and far layer settings to their defaults." };
        reset.Pressed += ResetEmitterGrid;
        section.AddChild(reset);
        _emitterGridStatus = new Label { Name = "EmitterGridStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_emitterGridStatus);
        RefreshEmitterGridEditable();
        RefreshEmitterGridStatus();
    }

    private CheckButton AddEmitterGridToggle(VBoxContainer parent, string name, string text, bool pressed, string tooltip)
    {
        var toggle = new CheckButton { Name = name, Text = text, ButtonPressed = pressed, TooltipText = tooltip, Disabled = _emitterGrid == null };
        toggle.Toggled += _ => QueueEmitterGridSettings();
        parent.AddChild(toggle);
        return toggle;
    }

    private HSlider AddEmitterGridSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _emitterGrid != null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueEmitterGridSettings(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueEmitterGridSettings()
    {
        if (_emitterGridDefaults == null || _emitterGridCore == null || _emitterGridCoreThreshold == null || _emitterGridCoreMinPixels == null || _emitterGridFar == null
            || _emitterGridNearMax == null || _emitterGridFollowClouds == null || _emitterGridHandoffStart == null || _emitterGridHandoffEnd == null) return;
        bool changed = _emitterGridDefaults.SetDepthWriteCore(_emitterGridCore.ButtonPressed);
        changed |= _emitterGridDefaults.SetCoreDepthThreshold((float)_emitterGridCoreThreshold.Value);
        changed |= _emitterGridDefaults.SetCoreDepthMinPixels((float)_emitterGridCoreMinPixels.Value);
        changed |= _emitterGridDefaults.SetFarLayerEnabled(_emitterGridFar.ButtonPressed);
        changed |= _emitterGridDefaults.SetNearLayerMaxKm((float)_emitterGridNearMax.Value);
        changed |= _emitterGridDefaults.SetFarHandoffFollowsClouds(_emitterGridFollowClouds.ButtonPressed);
        changed |= _emitterGridDefaults.SetFarHandoffStartKm((float)_emitterGridHandoffStart.Value);
        changed |= _emitterGridDefaults.SetFarHandoffEndKm((float)_emitterGridHandoffEnd.Value);
        _emitterGridDefaults.Apply(_emitterGrid);
        RefreshEmitterGridEditable();
        if (!changed) return;
        _emitterGridSaveDelay = .75;
        RefreshEmitterGridStatus();
    }

    private void ResetEmitterGrid()
    {
        _emitterGridDefaults?.Reset();
        _emitterGridDefaults?.Apply(_emitterGrid);
        RefreshEmitterGridControls();
        FlushEmitterGridDefaults();
    }

    private void RefreshEmitterGridControls()
    {
        if (_emitterGridDefaults == null) return;
        var s = _emitterGridDefaults.Settings;
        _emitterGridCore?.SetPressedNoSignal(s.DepthWriteCore);
        SetEmitterSliderNoSignal(_emitterGridCoreThreshold, s.CoreDepthThreshold);
        SetEmitterSliderNoSignal(_emitterGridCoreMinPixels, s.CoreDepthMinPixels);
        _emitterGridFar?.SetPressedNoSignal(s.FarLayerEnabled);
        SetEmitterSliderNoSignal(_emitterGridNearMax, s.NearLayerMaxKm);
        _emitterGridFollowClouds?.SetPressedNoSignal(s.FarHandoffFollowsClouds);
        SetEmitterSliderNoSignal(_emitterGridHandoffStart, s.FarHandoffStartKm);
        SetEmitterSliderNoSignal(_emitterGridHandoffEnd, s.FarHandoffEndKm);
        RefreshEmitterGridEditable();
        RefreshEmitterGridStatus();
    }

    private void RefreshEmitterGridEditable()
    {
        bool present = _emitterGrid != null && GodotObject.IsInstanceValid(_emitterGrid);
        bool manual = present && _emitterGridFollowClouds?.ButtonPressed == false;
        foreach (HSlider? slider in new[] { _emitterGridCoreThreshold, _emitterGridCoreMinPixels, _emitterGridNearMax })
            if (slider != null && slider.Editable != present) slider.Editable = present;
        foreach (HSlider? slider in new[] { _emitterGridHandoffStart, _emitterGridHandoffEnd })
            if (slider != null && slider.Editable != manual) slider.Editable = manual;
        foreach (CheckButton? toggle in new[] { _emitterGridCore, _emitterGridFar, _emitterGridFollowClouds })
            if (toggle != null && toggle.Disabled == present) toggle.Disabled = !present;
    }

    private void RefreshEmitterGridStatus()
    {
        if (_emitterGridStatus == null || _emitterGridDefaults == null) return;
        _emitterGridStatus.Text = _emitterGridDefaults.LastError.Length > 0 ? "Save error: " + _emitterGridDefaults.LastError
            : _emitterGridDefaults.Dirty ? "Emitter relay grid settings pending save." : "Emitter relay grid settings saved.";
    }
}
