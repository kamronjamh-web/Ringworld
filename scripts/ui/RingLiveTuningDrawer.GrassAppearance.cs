using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private void BuildGrassAppearanceControls(VBoxContainer tab)
    {
        VBoxContainer appearance = AddGrassGroup(tab, "Biome appearance", "Choose density, height and tint for each environment. Both blade faces use ground-facing illumination by default. Enable legacy blade side shading for directional blade darkening. Terrain color match applies at every distance, retaining blade brightness variation nearby. The distance band finishes ground lighting and brightness convergence before roots disappear.");
        AddGrassToggle(appearance, "EnableGrassAppearance", "Enable biome grass appearance");
        AddGrassToggle(appearance, "EnableGrassBladeShading", "Blade side shading (legacy)");
        AddGrassSlider(appearance, "GrasslandBiomeDensity", "Grassland density", 0, 2, .01);
        AddGrassSlider(appearance, "ForestBiomeDensity", "Forest density", 0, 2, .01);
        AddGrassSlider(appearance, "SparseBiomeDensity", "Sparse density", 0, 2, .01);
        AddGrassSlider(appearance, "GrasslandBiomeHeight", "Grassland height", .25, 2, .01);
        AddGrassSlider(appearance, "ForestBiomeHeight", "Forest height", .25, 2, .01);
        AddGrassSlider(appearance, "SparseBiomeHeight", "Sparse height", .25, 2, .01);
        AddGrassSlider(appearance, "GrassTerrainHandoffStrength", "Terrain color match", 0, 1, .01);
        AddGrassSlider(appearance, "GrassTerrainBlendStartPercent", "Ground convergence start (% of draw distance)", 0, 99, 1);
        AddGrassSlider(appearance, "GrassTerrainBlendEndPercent", "Ground convergence end (% of draw distance)", 1, 100, 1);
        AddGrassSlider(appearance, "GrassTerrainLightingMatch", "Ground lighting match", 0, 1, .01);
        AddGrassAppearanceColor(appearance, "GrasslandBiomeTint", "Grassland tint");
        AddGrassAppearanceColor(appearance, "ForestBiomeTint", "Forest tint");
        AddGrassAppearanceColor(appearance, "SparseBiomeTint", "Sparse tint");

        VBoxContainer modifiers = AddGrassGroup(tab, "Local ground detail", "A placed test zone can thin and tint grass for paths, worn ground, or future gameplay marks.");
        AddGrassToggle(modifiers, "EnableGrassModifiers", "Enable local ground detail");
        AddGrassSlider(modifiers, "GrassModifierDensityStrength", "Density response", 0, 1, .01);
        AddGrassSlider(modifiers, "GrassModifierTintStrength", "Tint response", 0, 1, .01);
        AddGrassToggle(modifiers, "GrassModifierPreviewEnabled", "Show placed test zone");
        AddGrassSlider(modifiers, "GrassModifierPreviewRadiusMeters", "Test zone radius (m)", .25, 128, .25);
        AddGrassSlider(modifiers, "GrassModifierPreviewFeatherMeters", "Test zone soft edge (m)", 0, 128, .25);
        AddGrassSlider(modifiers, "GrassModifierPreviewDensity", "Test zone density", 0, 1, .01);
        AddGrassAppearanceColor(modifiers, "GrassModifierPreviewTint", "Test zone tint");
        AddGrassSlider(modifiers, "GrassModifierPreviewTintStrength", "Test zone tint strength", 0, 1, .01);
        AddGrassModifierPreviewButton(modifiers);
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
