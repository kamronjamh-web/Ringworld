using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private void BuildGrassAppearanceControls(VBoxContainer tab)
    {
        VBoxContainer appearance = AddGrassGroup(tab, "Biome appearance", "Choose density, height and tint for each environment, and how grass blends into distant terrain.");
        AddGrassToggle(appearance, "EnableGrassAppearance", "Enable biome grass appearance");
        AddGrassSlider(appearance, "GrasslandBiomeDensity", "Grassland density", 0, 2, .01);
        AddGrassSlider(appearance, "ForestBiomeDensity", "Forest density", 0, 2, .01);
        AddGrassSlider(appearance, "SparseBiomeDensity", "Sparse density", 0, 2, .01);
        AddGrassSlider(appearance, "GrasslandBiomeHeight", "Grassland height", .25, 2, .01);
        AddGrassSlider(appearance, "ForestBiomeHeight", "Forest height", .25, 2, .01);
        AddGrassSlider(appearance, "SparseBiomeHeight", "Sparse height", .25, 2, .01);
        AddGrassSlider(appearance, "GrassTerrainHandoffStrength", "Terrain color handoff", 0, 1, .01);
        AddGrassAppearanceColor(appearance, "GrasslandBiomeTint", "Grassland tint");
        AddGrassAppearanceColor(appearance, "ForestBiomeTint", "Forest tint");
        AddGrassAppearanceColor(appearance, "SparseBiomeTint", "Sparse tint");
    }

    private void AddGrassAppearanceColor(VBoxContainer tab, string key, string label)
    {
        tab.AddChild(new Label { Text = label });
        _grassControlKeys.Add(key);
        var picker = new ColorPickerButton
        {
            Name = key, CustomMinimumSize = new Vector2(100, 46),
            Color = GrassValue(key).VariantType == Variant.Type.Color ? GrassValue(key).AsColor() : Colors.White,
            EditAlpha = false, EditIntensity = false, Disabled = !GrassAvailable
        };
        picker.ColorChanged += color => SetGrassTuningValue(key, color);
        tab.AddChild(picker);
    }
}
