using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private const string RenderResolutionPauseMenuPath = "../LodPauseMenu";
    private OptionButton? _renderResolutionOptions;
    private CheckButton? _adaptiveRenderResolution;
    private HSlider? _fsrSharpness;
    private HSlider? _mipBiasOffset;
    private CheckButton? _disableCloudInterleaveUnderFsr;
    private CheckButton? _farViewportFollowsFsrScale;
    private CheckButton? _compositorJitterCompensation;
    private Label? _renderResolutionStatus;
    private Label? _renderResolutionSaveStatus;

    private void BuildRenderResolutionControls(VBoxContainer tab)
    {
        VBoxContainer section = TuningMenuUi.CreateSection(tab, "Render resolution", "Internal 3D render scale. Native is the default.");
        _renderResolutionOptions = new OptionButton { Name = "RenderResolutionPreset", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int preset = 0; preset <= 5; preset++)
            _renderResolutionOptions.AddItem(RingLodPauseMenu.GetRenderResolutionPresetLabel(preset), preset);
        _renderResolutionOptions.ItemSelected += index =>
        {
            RingLodPauseMenu? pauseMenu = GetRenderResolutionPauseMenu();
            if (pauseMenu != null) pauseMenu.SetRenderResolutionPreset(_renderResolutionOptions.GetItemId((int)index));
            RefreshRenderResolutionControls();
        };
        section.AddChild(_renderResolutionOptions);

        _adaptiveRenderResolution = new CheckButton
        {
            Name = "AdaptiveRenderResolution",
            Text = "Automatic resolution",
            TooltipText = "Off by default. When enabled, reduces render scale under GPU pressure. Memory safeguards remain separate."
        };
        _adaptiveRenderResolution.Toggled += enabled =>
        {
            RingLodPauseMenu? pauseMenu = GetRenderResolutionPauseMenu();
            if (pauseMenu != null) pauseMenu.SetAdaptiveRenderResolution(enabled);
            RefreshRenderResolutionControls();
        };
        section.AddChild(_adaptiveRenderResolution);

        _fsrSharpness = AddRenderResolutionSlider(section, "FsrSharpness", "FSR sharpness (0 = sharpest)", 0, 2, 0.05, GameplayRenderResolutionDefaults.DefaultFsrSharpness,
            "Godot FSR sharpness: 0 is sharpest, each whole step halves it. Default 0.8; the old fixed value 0.2 over-sharpened the temporal image.",
            value => GetRenderResolutionPauseMenu()?.SetFsrSharpness((float)value));
        _mipBiasOffset = AddRenderResolutionSlider(section, "MipBiasOffset", "Mip bias offset (FSR only)", 0, 1, 0.05, 0,
            "Added on top of Godot's automatic log2(render scale) mipmap bias while FSR 2.2 is active. Positive values blur textures slightly and reduce shimmer.",
            value => GetRenderResolutionPauseMenu()?.SetMipBiasOffset((float)value));
        _disableCloudInterleaveUnderFsr = AddRenderResolutionToggle(section, "DisableCloudInterleaveUnderFsr", "Disable cloud interleave under FSR",
            "On by default. While FSR 2.2 is active Sunshine CloudInterleave is forced to 0; your cloud setting is restored when FSR is off.",
            enabled => GetRenderResolutionPauseMenu()?.SetDisableCloudInterleaveUnderFsr(enabled));
        _farViewportFollowsFsrScale = AddRenderResolutionToggle(section, "FarViewportFollowsFsrScale", "Far viewport follows FSR scale",
            "On by default. While FSR 2.2 is active the far-ring viewports are also scaled by the FSR render scale, saving fill rate.",
            enabled => GetRenderResolutionPauseMenu()?.SetFarViewportFollowsFsrScale(enabled));
        _compositorJitterCompensation = AddRenderResolutionToggle(section, "CompositorJitterCompensation", "Compositor jitter compensation",
            "On by default. While FSR 2.2 or TAA jitters the camera, Sunshine clouds and the far backdrop are resampled at the jitter offset so they stop shaking at horizon and cloud edges.",
            enabled => GetRenderResolutionPauseMenu()?.SetCompositorJitterCompensation(enabled));

        _renderResolutionStatus = new Label { Name = "RenderResolutionRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_renderResolutionStatus);

        var row = new HBoxContainer();
        var save = new Button { Name = "RenderResolutionSaveNow", Text = "Save render resolution", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () =>
        {
            GetRenderResolutionPauseMenu()?.FlushRenderResolutionSettings();
            RefreshRenderResolutionControls();
        };
        row.AddChild(save);
        var reset = new Button { Name = "RenderResolutionResetDefaults", Text = "Reset render resolution", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () =>
        {
            GetRenderResolutionPauseMenu()?.ResetRenderResolutionSettings();
            RefreshRenderResolutionControls();
        };
        row.AddChild(reset);
        tab.AddChild(row);
        _renderResolutionSaveStatus = new Label { Name = "RenderResolutionSettingsStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_renderResolutionSaveStatus);
        RefreshRenderResolutionControls();
    }

    private HSlider AddRenderResolutionSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double fallback, string tooltip, System.Action<double> apply)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(180, 0) });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = fallback, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valueLabel = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value =>
        {
            valueLabel.Text = value.ToString("0.##");
            apply(value);
            RefreshRenderResolutionControls();
        };
        row.AddChild(slider);
        row.AddChild(valueLabel);
        parent.AddChild(row);
        return slider;
    }

    private CheckButton AddRenderResolutionToggle(VBoxContainer parent, string name, string label, string tooltip, System.Action<bool> apply)
    {
        var toggle = new CheckButton { Name = name, Text = label, TooltipText = tooltip };
        toggle.SetPressedNoSignal(true); // both FSR toggles default on; Refresh syncs the real value once the pause menu is ready
        toggle.Toggled += enabled =>
        {
            apply(enabled);
            RefreshRenderResolutionControls();
        };
        parent.AddChild(toggle);
        return toggle;
    }

    private static void SetRenderResolutionSliderNoSignal(HSlider? slider, double value, bool ready)
    {
        if (slider == null) return;
        slider.Editable = ready;
        if (!ready || Mathf.IsEqualApprox((float)slider.Value, (float)value)) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent()?.GetChildCount() > 2 && slider.GetParent().GetChild(2) is Label valueLabel) valueLabel.Text = value.ToString("0.##");
    }

    private RingLodPauseMenu? GetRenderResolutionPauseMenu() => GetNodeOrNull<RingLodPauseMenu>(RenderResolutionPauseMenuPath);

    private void RefreshRenderResolutionControls()
    {
        RingLodPauseMenu? pauseMenu = GetRenderResolutionPauseMenu();
        Godot.Collections.Dictionary diagnostics = pauseMenu?.GetRenderResolutionDiagnostics() ?? new Godot.Collections.Dictionary();
        bool ready = GetDiagnosticBool(diagnostics, "ready");
        bool fsrAvailable = GetDiagnosticBool(diagnostics, "fsr_available");
        int preset = GetDiagnosticInt(diagnostics, "preset");
        bool adaptive = GetDiagnosticBool(diagnostics, "adaptive_enabled");

        if (_renderResolutionOptions != null)
        {
            _renderResolutionOptions.Disabled = !ready;
            for (int item = 1; item < _renderResolutionOptions.ItemCount; item++)
                _renderResolutionOptions.SetItemDisabled(item, !fsrAvailable);
            if (ready && _renderResolutionOptions.Selected != preset) _renderResolutionOptions.Select(preset);
        }
        if (_adaptiveRenderResolution != null)
        {
            _adaptiveRenderResolution.Disabled = !ready;
            if (ready && _adaptiveRenderResolution.ButtonPressed != adaptive) _adaptiveRenderResolution.SetPressedNoSignal(adaptive);
        }

        SetRenderResolutionSliderNoSignal(_fsrSharpness, diagnostics.TryGetValue("fsr_sharpness", out Variant sharpness) ? sharpness.AsDouble() : GameplayRenderResolutionDefaults.DefaultFsrSharpness, ready);
        SetRenderResolutionSliderNoSignal(_mipBiasOffset, diagnostics.TryGetValue("mip_bias_offset", out Variant mipBias) ? mipBias.AsDouble() : 0.0, ready);
        if (_disableCloudInterleaveUnderFsr != null)
        {
            _disableCloudInterleaveUnderFsr.Disabled = !ready;
            bool value = !diagnostics.ContainsKey("disable_cloud_interleave_under_fsr") || GetDiagnosticBool(diagnostics, "disable_cloud_interleave_under_fsr");
            if (ready && _disableCloudInterleaveUnderFsr.ButtonPressed != value) _disableCloudInterleaveUnderFsr.SetPressedNoSignal(value);
        }
        if (_farViewportFollowsFsrScale != null)
        {
            _farViewportFollowsFsrScale.Disabled = !ready;
            bool value = !diagnostics.ContainsKey("far_viewport_follows_fsr_scale") || GetDiagnosticBool(diagnostics, "far_viewport_follows_fsr_scale");
            if (ready && _farViewportFollowsFsrScale.ButtonPressed != value) _farViewportFollowsFsrScale.SetPressedNoSignal(value);
        }
        if (_compositorJitterCompensation != null)
        {
            _compositorJitterCompensation.Disabled = !ready;
            bool value = !diagnostics.ContainsKey("compositor_jitter_compensation") || GetDiagnosticBool(diagnostics, "compositor_jitter_compensation");
            if (ready && _compositorJitterCompensation.ButtonPressed != value) _compositorJitterCompensation.SetPressedNoSignal(value);
        }

        string requestedScale = GetDiagnosticPercentage(diagnostics, "requested_scale");
        string currentScale = GetDiagnosticPercentage(diagnostics, "current_scale");
        string error = GetDiagnosticText(diagnostics, "last_error");
        bool dirty = GetDiagnosticBool(diagnostics, "dirty");
        if (_renderResolutionStatus != null)
            _renderResolutionStatus.Text = !ready
                ? "Render resolution is waiting for the pause menu."
                : $"Requested: {requestedScale} | Current: {currentScale}";
        if (_renderResolutionSaveStatus != null)
            _renderResolutionSaveStatus.Text = !ready
                ? "Render-resolution settings unavailable."
                : error.Length > 0 ? $"Render-resolution save error: {error}"
                : dirty ? "Render-resolution settings pending save." : "Render-resolution settings saved.";
    }

    private static bool GetDiagnosticBool(Godot.Collections.Dictionary diagnostics, string key) =>
        diagnostics.TryGetValue(key, out Variant value) && value.AsBool();

    private static int GetDiagnosticInt(Godot.Collections.Dictionary diagnostics, string key) =>
        diagnostics.TryGetValue(key, out Variant value) ? value.AsInt32() : 0;

    private static string GetDiagnosticText(Godot.Collections.Dictionary diagnostics, string key) =>
        diagnostics.TryGetValue(key, out Variant value) ? value.ToString() : "";

    private static string GetDiagnosticPercentage(Godot.Collections.Dictionary diagnostics, string key) =>
        diagnostics.TryGetValue(key, out Variant value) ? $"{value.AsSingle() * 100f:0}%" : "-";
}
