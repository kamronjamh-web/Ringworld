using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private const string RenderResolutionPauseMenuPath = "../LodPauseMenu";
    private OptionButton? _renderResolutionOptions;
    private CheckButton? _adaptiveRenderResolution;
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
