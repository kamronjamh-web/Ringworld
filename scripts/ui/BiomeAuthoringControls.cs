using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Shared, rule-based biome authoring fields used by the editor dock and F6 drawer.</summary>
[Tool]
public partial class BiomeAuthoringControls : VBoxContainer
{
    private Resource? _preset;
    [Signal] public delegate void ChangedEventHandler();

    public void Configure(Resource preset)
    {
        _preset = preset;
        foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        Build();
    }

    private void Build()
    {
        if (_preset == null) return;
        AddText("Name", "DisplayName");
        AddNumber("Seed", "Seed", int.MinValue, int.MaxValue, 1);
        AddChoice("Base biome", "BaseBiome", new[] { "Tundra", "Desert", "Grassland", "Forest", "Alpine" }, new[] { 3, 4, 5, 6, 7 });
        Group("Terrain");
        AddNumber("Elevation (m)", "MeanElevationMeters", -1000, 8000, 1);
        AddNumber("Variation (m)", "ElevationVariationMeters", 0, 1000, 1);
        AddNumber("Broad scale (m)", "BroadFeatureScaleMeters", 100, 10000, 10);
        AddNumber("Detail amplitude (m)", "DetailAmplitudeMeters", 0, 500, 1);
        AddNumber("Detail scale (m)", "DetailFeatureScaleMeters", 20, 2000, 1);
        Group("Climate");
        AddNumber("Humidity", "Humidity", 0, 1, .01);
        AddNumber("Temperature", "Temperature", 0, 1, .01);
        Group("Trees");
        AddNumber("Density", "TreeDensity", 0, 1, .01);
        AddNumber("Spacing (m)", "MinimumTreeSpacingMeters", 2, 64, .25);
        AddNumber("Clustering", "TreeClustering", 0, 1, .01);
        AddNumber("Clearings", "ClearingStrength", 0, 1, .01);
        AddNumber("Maple weight", "MapleWeight", 0, 1, .01);
        AddNumber("Fir weight", "FirWeight", 0, 1, .01);
        AddNumber("Pine weight", "PineWeight", 0, 1, .01);
        AddNumber("Min height (m)", "MinimumTreeHeightMeters", 1, 80, .1);
        AddNumber("Max height (m)", "MaximumTreeHeightMeters", 1, 80, .1);
        AddNumber("Maximum slope (deg)", "MaximumTreeSlopeDegrees", 0, 60, 1);
        Group("Grass");
        AddNumber("Density", "GrassDensity", 0, 2, .01);
        AddNumber("Height (m)", "GrassHeightMeters", .1, 4, .01);
        AddColor("Tint", "GrassTint");
        Group("Ground");
        AddChoice("Material", "GroundMaterial", new[] { "Grass soil", "Forest floor", "Desert sand", "Tundra ground", "Alpine rock" }, new[] { 0, 1, 2, 3, 4 });
        AddColor("Palette tint", "GroundTint");
        AddNumber("Texture scale", "GroundTextureScale", .1, 32, .01);
    }

    private void Group(string title) => AddChild(new Label { Text = title, ThemeTypeVariation = "HeaderSmall" });
    private Variant Value(string property) => _preset?.Get(property) ?? default;
    private void Set(string property, Variant value) { _preset?.Set(property, value); EmitSignal(SignalName.Changed); }

    private void AddText(string label, string property)
    {
        AddChild(new Label { Text = label });
        var line = new LineEdit { Name = property, Text = Value(property).AsString() };
        line.TextChanged += value => Set(property, value);
        AddChild(line);
    }

    private void AddNumber(string label, string property, double min, double max, double step)
    {
        AddChild(new Label { Text = label });
        Variant current = Value(property);
        double number = current.VariantType is Variant.Type.Int or Variant.Type.Float ? current.AsDouble() : min;
        var row = new HBoxContainer();
        var slider = new HSlider { Name = property + "Slider", MinValue = min, MaxValue = max, Step = step, Value = number, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var spin = new SpinBox { Name = property, MinValue = min, MaxValue = max, Step = step, Value = number, CustomMinimumSize = new Vector2(88, 0) };
        slider.ValueChanged += value => { spin.SetValueNoSignal(value); Set(property, value); };
        spin.ValueChanged += value => { slider.SetValueNoSignal(value); Set(property, value); };
        row.AddChild(slider); row.AddChild(spin); AddChild(row);
    }

    private void AddColor(string label, string property)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Variant current = Value(property);
        var picker = new ColorPickerButton { Name = property, Color = current.VariantType == Variant.Type.Color ? current.AsColor() : Colors.White };
        picker.ColorChanged += value => Set(property, value);
        row.AddChild(picker); AddChild(row);
    }

    private void AddChoice(string label, string property, string[] choices, int[] ids)
    {
        AddChild(new Label { Text = label });
        Variant current = Value(property);
        int selectedId = current.VariantType is Variant.Type.Int or Variant.Type.Float ? (int)current.AsInt64() : ids[0];
        var choice = new OptionButton { Name = property };
        for (int i = 0; i < choices.Length; i++) choice.AddItem(choices[i], ids[i]);
        choice.Select(Mathf.Max(0, choice.GetItemIndex(selectedId)));
        choice.ItemSelected += index => Set(property, choice.GetItemId((int)index));
        AddChild(choice);
    }
}
