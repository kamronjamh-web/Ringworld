using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Recipe controls for the isolated GDScript terrain reference preview.</summary>
public partial class RingLiveTuningDrawer
{
    private Label? _generationPreviewStatus;
    private readonly System.Collections.Generic.List<(HSlider Slider, Label Value, string Property, bool DetailOnly, bool CoastalOnly, bool EnhancedOnly)> _generationPreviewControls = new();
    private readonly System.Collections.Generic.List<(SpinBox Spin, string Property, bool DetailOnly, bool CoastalOnly)> _generationPreviewSpins = new();
    private readonly System.Collections.Generic.List<(OptionButton Option, string Property, bool DetailOnly, bool CoastalOnly)> _generationPreviewOptions = new();
    private Control? _generationPreviewCoastalSection;
    private Control? _generationPreviewDetailSection;
    private Label? _generationPreviewRecipeIdentity;
    private Label? _generationPreviewLegacyCoastalNote;
    private Button? _generationPreviewUpgradeErosion;
    private double _generationPreviewRefreshDelay;

    private void BuildGenerationPreviewTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Terrain3D");
        tab.AddChild(new Label { Text = "REFERENCE GENERATION PREVIEW", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "Shape controls regenerate this preview after a short pause. Use Save recipe to keep your changes.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        if (_generationPreview == null)
            return;

        Resource? recipe = _generationPreview.Call("get_recipe_snapshot").AsGodotObject() as Resource;
        if (recipe == null)
        {
            tab.AddChild(new Label { Text = "The generation recipe is unavailable.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }
        string generatorId = recipe.Get("generator_id").AsString();
        bool coastalRecipe = generatorId == "ringworld_surface_coastal_v2" || generatorId == "ringworld_surface_detail_v3" || generatorId == "ringworld_surface_erosion_v4";
        bool detailRecipe = generatorId == "ringworld_surface_detail_v3" || generatorId == "ringworld_surface_erosion_v4";
        bool enhancedErosion = generatorId == "ringworld_surface_erosion_v4";
        _generationPreviewRecipeIdentity = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        _generationPreviewRecipeIdentity.Text = RecipeIdentityText(generatorId);
        tab.AddChild(_generationPreviewRecipeIdentity);
        VBoxContainer settings = TuningMenuUi.CreateSection(tab, "Recipe shape settings");
        AddRecipeSpin(settings, recipe, "seed", "Seed", 0, 2147483647, 1);
        AddRecipeSlider(settings, recipe, "land_balance", "Land / ocean balance", -.35, .35, .01);
        AddRecipeSlider(settings, recipe, "continental_scale_m", "Continental scale (m)", 50000, 600000, 1000);
        AddRecipeSlider(settings, recipe, "mountain_scale_m", "Range scale (m)", 10000, 250000, 1000);
        AddRecipeSlider(settings, recipe, "basin_depth_m", "Basin depth (m)", 1250, 7500, 50);
        _generationPreviewLegacyCoastalNote = new Label { Text = "Legacy v1 recipe preserved; coastal controls are unavailable.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted, Visible = !coastalRecipe };
        settings.AddChild(_generationPreviewLegacyCoastalNote);
        AddRecipeSlider(settings, recipe, "trench_depth_m", "Trench depth (m)", 0, 6000, 50);
        AddRecipeSlider(settings, recipe, "trench_threshold", "Trench rarity", .5, .98, .01);
        AddRecipeSlider(settings, recipe, "mountain_amplitude_m", "Mountain amplitude (m)", 0, 7500, 50);
        AddRecipeSlider(settings, recipe, "valley_width", "Valley width", .01, .75, .01);
        AddRecipeSlider(settings, recipe, "flattening", "Lowland flattening", 0, 8, .1);
        AddRecipeSlider(settings, recipe, "ridge_scale_m", "Ridge scale (m)", 1000, 30000, 250);
        AddRecipeSlider(settings, recipe, "ridge_sharpness", "Ridge sharpness", .1, 4, .05);
        AddRecipeSlider(settings, recipe, "secondary_amplitude_m", "Secondary relief (m)", 0, 200, 1);
        AddRecipeSlider(settings, recipe, "micro_amplitude_m", "Micro relief (m)", 0, 12, .1);
        AddRecipeSlider(settings, recipe, "detail_start_m", "Detail starts (m)", 0, 6000, 50);
        AddRecipeSlider(settings, recipe, "detail_full_m", "Detail full (m)", 0, 8000, 50);
        AddRecipeSlider(settings, recipe, "warp_amplitude_m", "Warp amplitude (m)", 0, 15000, 100);
        AddRecipeSlider(settings, recipe, "noise_gain", "Noise gain", 0, 1, .01);
        AddRecipeSlider(settings, recipe, "secondary_scale_m", "Secondary scale (m)", 16, 10000, 16);
        AddRecipeSlider(settings, recipe, "micro_scale_m", "Micro scale (m)", 16, 1000, 4);
        AddRecipeSpin(settings, recipe, "continental_octaves", "Continental octaves", 1, 8, 1);
        AddRecipeSpin(settings, recipe, "mountain_octaves", "Mountain octaves", 1, 8, 1);
        AddRecipeSpin(settings, recipe, "ridge_octaves", "Ridge octaves", 1, 8, 1);
        VBoxContainer coastalSettings = TuningMenuUi.CreateSection(tab, "Coastal shaping");
        _generationPreviewCoastalSection = coastalSettings.GetParent()?.GetParent() as Control ?? coastalSettings;
        _generationPreviewCoastalSection.Visible = coastalRecipe;
        AddRecipeSlider(coastalSettings, recipe, "beach_slope", "Beach slope", .005, .1, .001, coastalOnly: true);
        AddRecipeSlider(coastalSettings, recipe, "shelf_width_m", "Shelf width approximation (m)", 2000, 100000, 500, coastalOnly: true);
        AddRecipeSlider(coastalSettings, recipe, "shelf_depth_m", "Shelf depth (m)", 50, 300, 5, coastalOnly: true);
        AddRecipeSlider(coastalSettings, recipe, "continental_slope_width_m", "Continental slope width (m)", 5000, 100000, 500, coastalOnly: true);
        VBoxContainer detailSettings = TuningMenuUi.CreateSection(tab, "Detail mountain shaping");
        _generationPreviewDetailSection = detailSettings.GetParent()?.GetParent() as Control ?? detailSettings;
        _generationPreviewDetailSection.Visible = detailRecipe;
        AddRecipeOption(detailSettings, recipe, "mountain_detail_mode", "Mountain detail mode", new[] { "Ridged spurs", "Swiss spurs", "Ridged spurs + gullies", "Swiss spurs + gullies" }, true);
        AddRecipeSlider(detailSettings, recipe, "foothill_amplitude_m", "Foothill amplitude (m)", 0, 240, 1, true);
        AddRecipeSlider(detailSettings, recipe, "foothill_scale_m", "Foothill scale (m)", 128, 8192, 1, true);
        AddRecipeSlider(detailSettings, recipe, "spur_amplitude_m", "Spur amplitude (m)", 0, 400, 1, true);
        AddRecipeSlider(detailSettings, recipe, "spur_scale_m", "Spur scale (m)", 128, 8192, 1, true);
        AddRecipeSlider(detailSettings, recipe, "erosion_strength_m", "Erosion strength (m)", 0, 160, 1, true);
        AddRecipeSlider(detailSettings, recipe, "erosion_scale_m", "Erosion scale (m)", 64, 2048, 1, true);
        AddRecipeSpin(detailSettings, recipe, "erosion_octaves", "Erosion octaves", 1, 4, 1, true);
        AddRecipeSlider(detailSettings, recipe, "swiss_warp", "Swiss warp", 0, 1, .01, true);
        AddRecipeSlider(detailSettings, recipe, "erosion_ridge_preservation", "Ridge preservation", 0, 1, .01, true, enhancedOnly: true);
        AddRecipeSlider(detailSettings, recipe, "gully_normalization", "Gully normalization", 0, 1, .01, true, enhancedOnly: true);
        if (detailRecipe && !enhancedErosion)
        {
            var upgrade = new Button { Text = "Use enhanced gullies" };
            upgrade.Pressed += () => { _generationPreview?.Call("upgrade_to_enhanced_erosion"); RefreshGenerationPreviewControls(); };
            detailSettings.AddChild(upgrade);
            _generationPreviewUpgradeErosion = upgrade;
        }

        var actions = new HBoxContainer();
        var regenerate = new Button { Text = "Regenerate", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        regenerate.Pressed += () => _generationPreview?.Call("regenerate");
        actions.AddChild(regenerate);
        var save = new Button { Text = "Save recipe", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => _generationPreview?.Call("save_recipe");
        actions.AddChild(save);
        tab.AddChild(actions);
        var reset = new Button { Text = "Reset recipe" };
        reset.Pressed += () => { _generationPreview?.Call("reset_recipe"); RefreshGenerationPreviewControls(); };
        tab.AddChild(reset);
        _generationPreviewStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_generationPreviewStatus);
    }

    private void AddRecipeSlider(VBoxContainer parent, Resource recipe, string property, string label, double min, double max, double step, bool detailOnly = false, bool coastalOnly = false, bool enhancedOnly = false)
    {
        string generatorId = recipe.Get("generator_id").AsString();
        bool unavailable = detailOnly && generatorId != "ringworld_surface_detail_v3" && generatorId != "ringworld_surface_erosion_v4" || coastalOnly && generatorId != "ringworld_surface_coastal_v2" && generatorId != "ringworld_surface_detail_v3" && generatorId != "ringworld_surface_erosion_v4" || enhancedOnly && generatorId != "ringworld_surface_erosion_v4";
        double initial = unavailable ? min : recipe.Get(property).AsDouble();
        parent.AddChild(new Label { Text = label });
        var row = new HBoxContainer();
        var slider = new HSlider { Name = property, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = !unavailable, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); _generationPreview?.Call("apply_recipe_setting", property, current); _generationPreviewRefreshDelay = 0; };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
        _generationPreviewControls.Add((slider, value, property, detailOnly, coastalOnly, enhancedOnly));
    }

    private void AddRecipeSpin(VBoxContainer parent, Resource recipe, string property, string label, double min, double max, double step, bool detailOnly = false, bool coastalOnly = false)
    {
        parent.AddChild(new Label { Text = label });
        string generatorId = recipe.Get("generator_id").AsString();
        bool unavailable = detailOnly && generatorId != "ringworld_surface_detail_v3" && generatorId != "ringworld_surface_erosion_v4" || coastalOnly && generatorId != "ringworld_surface_coastal_v2" && generatorId != "ringworld_surface_detail_v3" && generatorId != "ringworld_surface_erosion_v4";
        double initial = unavailable ? min : recipe.Get(property).AsDouble();
        var spin = new SpinBox { Name = property, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = !unavailable, AllowGreater = false, AllowLesser = false };
        spin.ValueChanged += value => _generationPreview?.Call("apply_recipe_setting", property, (long)Math.Round(value));
        parent.AddChild(spin);
        _generationPreviewSpins.Add((spin, property, detailOnly, coastalOnly));
    }

    private void AddRecipeOption(VBoxContainer parent, Resource recipe, string property, string label, string[] choices, bool detailOnly = false, bool coastalOnly = false)
    {
        parent.AddChild(new Label { Text = label });
        var option = new OptionButton { Name = property };
        foreach (string choice in choices) option.AddItem(choice);
        int initial = detailOnly && recipe.Get("generator_id").AsString() != "ringworld_surface_detail_v3" && recipe.Get("generator_id").AsString() != "ringworld_surface_erosion_v4" ? 0 : recipe.Get(property).AsInt32();
        option.Select(Math.Clamp(initial, 0, choices.Length - 1));
        option.ItemSelected += index => _generationPreview?.Call("apply_recipe_setting", property, (long)index);
        parent.AddChild(option);
        _generationPreviewOptions.Add((option, property, detailOnly, coastalOnly));
    }

    private void RefreshGenerationPreviewControls()
    {
        if (_generationPreview == null) return;
        Resource? recipe = _generationPreview.Call("get_recipe_snapshot").AsGodotObject() as Resource;
        if (recipe == null) return;
        string generatorId = recipe.Get("generator_id").AsString();
        bool detailRecipe = generatorId == "ringworld_surface_detail_v3" || generatorId == "ringworld_surface_erosion_v4";
        bool coastalRecipe = generatorId == "ringworld_surface_coastal_v2" || detailRecipe;
        bool enhancedErosion = generatorId == "ringworld_surface_erosion_v4";
        if (_generationPreviewCoastalSection != null) _generationPreviewCoastalSection.Visible = coastalRecipe;
        if (_generationPreviewDetailSection != null) _generationPreviewDetailSection.Visible = detailRecipe;
        if (_generationPreviewLegacyCoastalNote != null) _generationPreviewLegacyCoastalNote.Visible = !coastalRecipe;
        if (_generationPreviewUpgradeErosion != null) _generationPreviewUpgradeErosion.Visible = generatorId == "ringworld_surface_detail_v3";
        if (_generationPreviewRecipeIdentity != null) _generationPreviewRecipeIdentity.Text = RecipeIdentityText(generatorId);
        foreach (var control in _generationPreviewControls)
        {
            bool unavailable = control.DetailOnly && !detailRecipe || control.CoastalOnly && !coastalRecipe || control.EnhancedOnly && !enhancedErosion;
            control.Slider.Editable = !unavailable;
            if (unavailable) continue;
            double current = recipe.Get(control.Property).AsDouble();
            control.Slider.SetValueNoSignal(current);
            control.Value.Text = current.ToString("0.###");
        }
        foreach (var control in _generationPreviewSpins)
        {
            bool unavailable = control.DetailOnly && !detailRecipe || control.CoastalOnly && !coastalRecipe;
            control.Spin.Editable = !unavailable;
            if (!unavailable) control.Spin.SetValueNoSignal(recipe.Get(control.Property).AsDouble());
        }
        foreach (var control in _generationPreviewOptions)
        {
            bool unavailable = control.DetailOnly && !detailRecipe || control.CoastalOnly && !coastalRecipe;
            control.Option.Disabled = unavailable;
            if (!unavailable) control.Option.Select(Math.Max(0, recipe.Get(control.Property).AsInt32()));
        }
    }

    private static string RecipeIdentityText(string generatorId) => generatorId == "ringworld_surface_erosion_v4"
        ? "Recipe: ringworld_surface_erosion_v4 (enhanced erosion enabled)."
        : generatorId == "ringworld_surface_detail_v3"
        ? "Recipe: ringworld_surface_detail_v3 (detail mountain shaping enabled)."
        : generatorId == "ringworld_surface_coastal_v2"
            ? "Recipe: ringworld_surface_coastal_v2. Reset recipe creates the enhanced erosion recipe; this saved recipe remains unchanged."
            : "Legacy v1 recipe preserved. Reset recipe creates the enhanced erosion recipe.";

    private void ProcessGenerationPreviewTuning(double delta)
    {
        _generationPreviewRefreshDelay -= delta;
        if (_generationPreviewRefreshDelay > 0) return;
        _generationPreviewRefreshDelay = .2;
        RefreshGenerationPreviewControls();
        if (_generationPreviewStatus == null || _generationPreview == null) return;
        Godot.Collections.Dictionary state = _generationPreview.Call("get_preview_diagnostics").AsGodotDictionary();
        string error = state.TryGetValue("error", out Variant errorValue) ? errorValue.AsString() : "";
        bool ready = state.TryGetValue("ready", out Variant readyValue) && readyValue.AsBool();
        long revision = state.TryGetValue("revision", out Variant revisionValue) ? revisionValue.AsInt64() : 0;
        long committed = state.TryGetValue("committed_revision", out Variant committedValue) ? committedValue.AsInt64() : revision;
        bool updating = state.TryGetValue("updating", out Variant updatingValue) ? updatingValue.AsBool() : !ready;
        _generationPreviewStatus.Text = error.Length > 0 ? error : ready
            ? updating ? $"Revision {committed} active; preparing revision {revision}." : $"Revision {committed} ready."
            : $"Generating revision {revision}.";
        RefreshGenerationStreamingDiagnostics(state);
    }

    private void RefreshGenerationStreamingDiagnostics(Godot.Collections.Dictionary state)
    {
        if (_generationStreamingDiagnostics == null) return;
        Godot.Collections.Dictionary stream = state.TryGetValue("stream", out Variant streamValue)
            ? streamValue.AsGodotDictionary() : new();
        Godot.Collections.Dictionary native = state.TryGetValue("native", out Variant nativeValue)
            ? nativeValue.AsGodotDictionary() : new();
        Godot.Collections.Dictionary preparation = native.TryGetValue("preparation", out Variant preparationValue)
            ? preparationValue.AsGodotDictionary() : new();
        string phase = preparation.TryGetValue("phase", out Variant phaseValue) ? phaseValue.AsString() : "Idle";
        long inflight = stream.TryGetValue("inflight", out Variant inflightValue) ? inflightValue.AsInt64() : 0;
        long pending = stream.TryGetValue("pending", out Variant pendingValue) ? pendingValue.AsInt64() : 0;
        long cacheBytes = stream.TryGetValue("cache_bytes", out Variant cacheValue) ? cacheValue.AsInt64() : 0;
        long cacheLimit = stream.TryGetValue("cache_limit_bytes", out Variant limitValue) ? limitValue.AsInt64() : 0;
        long tilesDone = preparation.TryGetValue("tiles_done", out Variant tilesDoneValue) ? tilesDoneValue.AsInt64() : 0;
        long tilesTotal = preparation.TryGetValue("tiles_total", out Variant tilesTotalValue) ? tilesTotalValue.AsInt64() : 0;
        double stepMax = preparation.TryGetValue("step_max_ms", out Variant stepValue) ? stepValue.AsDouble() : 0;
        long uploadOverruns = preparation.TryGetValue("upload_budget_overruns", out Variant uploadOverrunValue) ? uploadOverrunValue.AsInt64() : 0;
        long collisionOverruns = preparation.TryGetValue("collision_budget_overruns", out Variant collisionOverrunValue) ? collisionOverrunValue.AsInt64() : 0;
        double uploadMs = preparation.TryGetValue("upload_ms", out Variant uploadValue) ? uploadValue.AsDouble() : 0;
        double shapeMs = preparation.TryGetValue("shape_ms", out Variant shapeValue) ? shapeValue.AsDouble() : 0;
        double installMs = preparation.TryGetValue("installation_ms", out Variant installValue) ? installValue.AsDouble() : 0;
        long edgeWaits = state.TryGetValue("edge_waits", out Variant edgeValue) ? edgeValue.AsInt64() : 0;
        _generationStreamingDiagnostics.Text = $"Preparation: {phase} | tiles {tilesDone}/{tilesTotal} | step {stepMax:0.##} ms\n"
            + $"Workers: {inflight} in flight, {pending} pending | cache {cacheBytes / 1048576.0:0.0}/{cacheLimit / 1048576.0:0.0} MiB\n"
            + $"Upload {uploadMs:0.##} ms | shape {shapeMs:0.##} ms | install {installMs:0.##} ms\n"
            + $"Budget overruns: upload {uploadOverruns}, collision {collisionOverruns} | edge waits {edgeWaits}";
    }
}
