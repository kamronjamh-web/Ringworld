using System;
using System.Linq;
using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private static void BuildSunblockerDemoTab(TabContainer tabs, global::RingworldTechDemo.SunblockerDemoController demo)
    {
        VBoxContainer tab = CreateTab(tabs, "Sunblocker");
        tab.AddChild(new Label { Text = "Standalone shadow-caster comparison. Demo settings save separately.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer controls = TuningMenuUi.CreateSection(tab, "Comparison controls");
        var elevation = AddSunblockerSlider(controls, "Sun elevation", 8, 80, 1, demo.SunElevation, v => demo.SunElevation = (float)v);
        var azimuth = AddSunblockerSlider(controls, "Sun azimuth", 0, 359, 1, demo.SunAzimuth, v => demo.SunAzimuth = (float)v);
        var atlas = AddSunblockerOption(controls, "Shadow atlas", new[] { "1024", "2048", "4096" }, Array.IndexOf(new[] { 1024, 2048, 4096 }, demo.ShadowAtlasSize), selected => demo.ShadowAtlasSize = new[] { 1024, 2048, 4096 }[selected]);
        var variants = AddSunblockerOption(controls, "Tree variant", demo.TreeVariantNames.ToArray(), demo.TreeVariantIndex, selected => demo.TreeVariantIndex = selected);
        var trees = AddSunblockerSlider(controls, "Trees / column", 4, 64, 1, demo.TreeCount, v => demo.TreeCount = Mathf.RoundToInt(v));
        var handoffDistance = AddSunblockerSlider(controls, "Distance handoff (m)", 100, 200, 5, demo.DistanceLodMeters, v => demo.DistanceLodMeters = (float)v);
        var handoff = AddSunblockerToggle(controls, "Distance handoff", demo.DistanceHandoffEnabled, v => demo.DistanceHandoffEnabled = v);
        var proxies = AddSunblockerToggle(controls, "Show proxy geometry", demo.ShowProxyGeometry, v => demo.ShowProxyGeometry = v);
        var single = AddSunblockerToggle(controls, "Single-method view", demo.SingleMethodView, v => demo.SingleMethodView = v);
        var method = AddSunblockerOption(controls, "Single method", new[] { "Full geometry (LOD0)", "Authored LOD2 proxy", "Sun-facing authored cards" }, demo.SingleMethod, selected => demo.SingleMethod = selected);
        var actions = new HBoxContainer();
        var resetCamera = new Button { Text = "Reset camera", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; resetCamera.Pressed += demo.ResetCamera; actions.AddChild(resetCamera);
        var resetSettings = new Button { Text = "Reset settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; resetSettings.Pressed += () => { demo.ResetSettings(); elevation.Value = demo.SunElevation; azimuth.Value = demo.SunAzimuth; atlas.Select(Array.IndexOf(new[] { 1024, 2048, 4096 }, demo.ShadowAtlasSize)); variants.Select(demo.TreeVariantIndex); trees.Value = demo.TreeCount; handoffDistance.Value = demo.DistanceLodMeters; handoff.SetPressedNoSignal(demo.DistanceHandoffEnabled); proxies.SetPressedNoSignal(demo.ShowProxyGeometry); single.SetPressedNoSignal(demo.SingleMethodView); method.Select(demo.SingleMethod); }; actions.AddChild(resetSettings);
        tab.AddChild(actions);
        tab.AddChild(new Label { Text = "Full geometry uses the imported tree LOD0. Authored LOD2 provides the shadow-only proxy, including any leaf cutouts. Authored LOD3 supplies sun-facing crossed cards.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
    }

    private static HSlider AddSunblockerSlider(VBoxContainer parent, string label, double min, double max, double step, double initial, System.Action<double> apply)
    {
        parent.AddChild(new Label { Text = label });
        var row = new HBoxContainer();
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); apply(current); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
        return slider;
    }

    private static CheckButton AddSunblockerToggle(VBoxContainer parent, string label, bool initial, System.Action<bool> apply)
    {
        var toggle = new CheckButton { Text = label, ButtonPressed = initial };
        toggle.Toggled += value => apply(value);
        parent.AddChild(toggle);
        return toggle;
    }

    private static OptionButton AddSunblockerOption(VBoxContainer parent, string label, string[] choices, int initial, System.Action<int> apply)
    {
        parent.AddChild(new Label { Text = label });
        var option = new OptionButton { FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int index = 0; index < choices.Length; index++)
        {
            option.AddItem(choices[index]);
            option.SetItemTooltip(index, choices[index]);
        }
        option.Select(Mathf.Clamp(initial, 0, choices.Length - 1));
        option.ItemSelected += selected => apply((int)selected);
        parent.AddChild(option);
        return option;
    }
}
