using Godot;

namespace RingworldTechDemo.UI;

/// <summary>F6 authoring mode for the isolated biome preview scene; it never changes gameplay defaults.</summary>
public partial class RingLiveTuningDrawer
{
    private Resource? _biomeWorkingPreset;
    private Label? _biomeStatus;
    private FileDialog? _biomeSaveDialog;
    private bool BiomeModalActive => _biomeSaveDialog?.Visible == true;

    private void BuildBiomeAuthoringTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Biome");
        tab.AddChild(new Label { Text = "AUTHORING PREVIEW", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "Tune this biome, then save it as a reusable preset.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        Resource? sourcePreset = _biomeAuthoringPreview?.Get("Preset").AsGodotObject() as Resource;
        _biomeWorkingPreset = sourcePreset?.Duplicate(true) as Resource;
        if (_biomeWorkingPreset == null)
        {
            tab.AddChild(new Label { Text = "Biome preset is unavailable on this preview scene.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }
        VBoxContainer controlsSection = TuningMenuUi.CreateSection(tab, "Biome settings");
        var controls = new BiomeAuthoringControls();
        controls.Configure(_biomeWorkingPreset);
        controls.Changed += ApplyBiomePreview;
        controlsSection.AddChild(controls);
        var save = new Button { Text = "Save biome preset as .tres" };
        save.Pressed += ShowBiomeSaveDialog;
        tab.AddChild(save);
        _biomeStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted, Text = "Working copy. Saving is explicit." };
        tab.AddChild(_biomeStatus);
    }

    private void ApplyBiomePreview()
    {
        if (_biomeAuthoringPreview == null || _biomeWorkingPreset == null) return;
        if (!_biomeWorkingPreset.Call("ValidateForUi").AsBool())
        {
            if (_biomeStatus != null) _biomeStatus.Text = "Preview retains the last valid build: " + _biomeWorkingPreset.Call("GetValidationError").AsString();
            return;
        }
        _biomeAuthoringPreview.Call("SetPreset", _biomeWorkingPreset);
        if (_biomeStatus != null) _biomeStatus.Text = "Unsaved changes applied to the authoring preview.";
    }

    private void ShowBiomeSaveDialog()
    {
        if (_biomeWorkingPreset == null) return;
        _biomeSaveDialog?.QueueFree();
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Resources, CurrentDir = "res://resources/biomes", Exclusive = true };
        _biomeSaveDialog = dialog;
        dialog.AddFilter("*.tres ; Biome preset");
        dialog.FileSelected += path =>
        {
            if (!path.EndsWith(".tres")) path += ".tres";
            Variant result = _biomeWorkingPreset.Call("SavePreset", path);
            if (_biomeStatus != null) _biomeStatus.Text = result.AsInt32() == (int)Error.Ok ? $"Saved {path}. World assignment remains separate." : "Biome preset could not be saved.";
        };
        AddChild(dialog);
        dialog.CloseRequested += () => { if (IsInstanceValid(dialog)) dialog.QueueFree(); _biomeSaveDialog = null; };
        dialog.PopupCenteredRatio(.7f);
    }
}
