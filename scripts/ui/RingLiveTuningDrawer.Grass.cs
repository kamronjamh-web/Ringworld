using System;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private RingGraphicsQualityController? _grassGraphics;
    private GameplayGrassDefaults _grassDefaults = null!;
    private Label? _grassStatus, _grassSaveStatus, _grassGenerationPrototypeStatus;
    private Label? _grassGroupingStatus;
    private Button? _grassGenerationPrototypeRun;
    private double _grassSaveDelay, _grassSaveRetryDelay, _grassRefreshDelay;
    private readonly System.Collections.Generic.HashSet<string> _grassControlKeys = new();
    private string? _grassDefaultsPathOverride;
    private void InitializeGrassTuning() { _grassGraphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath); _grassDefaults = new GameplayGrassDefaults(_grassDefaultsPathOverride); _grassDefaults.Initialize(_grassGraphics); }
    private void ProcessGrassTuning(double delta)
    {
        if (_open && (_grassRefreshDelay -= delta) <= 0) { _grassRefreshDelay = .25; RefreshGrassControls(); }
        if (_grassDefaults.Dirty) { _grassSaveDelay -= delta; _grassSaveRetryDelay -= delta; if (_grassSaveDelay <= 0 && _grassSaveRetryDelay <= 0 && !FlushGrassDefaults()) _grassSaveRetryDelay = 5; }
    }
    private void BuildGrassTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Grass"); tab.AddChild(new Label { Text = "Live grass streaming controls. Only values you change are saved; other values follow the active graphics profile.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        var style = AddGrassGroup(tab, "Render style", "Blades is the established grass. Cards draws terrain-coloured clump cards out to the reach with far fewer instances. Switching resets and rebuilds all grass; the blade settings below only apply to Blades.");
        AddGrassRenderStyle(style);
        BuildGrassCardControls(tab);
        var gpuCulling = AddGrassGroup(tab, "GPU culling", "GPU compaction keeps the canonical CPU placement buffer. Unsupported renderers retain CPU and vertex-LOD rendering."); AddGrassToggle(gpuCulling, "UseGpuCulling", "Use GPU grass culling"); AddGrassToggle(gpuCulling, "GpuFrustumCulling", "Cull grass outside the camera view"); AddGrassSlider(gpuCulling, "GrassFrustumPaddingMeters", "View-edge safety margin (m)", 0, 8, .01); AddGrassSlider(gpuCulling, "GrassCardComputeMarginMeters", "Cards GPU distance margin (m)", 0, 8, .25); AddGrassToggle(gpuCulling, "SkipUnchangedGpuCulling", "Skip unchanged GPU culling updates"); AddGrassToggle(gpuCulling, "GroupGrassComputePhases", "Group grass compute phases"); AddGrassToggle(gpuCulling, "CullGrassTiles", "Skip invisible grass tiles and unused LODs"); AddGrassDebouncedToggle(gpuCulling, "UseGrassClumps", "Group grass into tuft instances");
        var grouping = AddGrassGroup(tab, "Adaptive grass groups", "Nearby grass retains individual tiles. Distant tiles share 2-by-2 groups, with optional 4-by-4 groups. This reduces renderer and culling bookkeeping while preserving grass density. Groups require compatible visible tiles beyond the near LOD transition and inside the far geometry distance. Source caps keep dense groups small; hysteresis steadies distance transitions.");
        AddGrassToggle(grouping, "EnableAdaptiveGrassGrouping", "Group distant grass tiles");
        AddGrassSlider(grouping, "GrassGroupingStartMeters", "2-by-2 group start distance (m)", 16, 1500, 1);
        AddGrassSlider(grouping, "GrassGroupingFarStartMeters", "4-by-4 group start distance (m)", 32, 2000, 1);
        AddGrassSlider(grouping, "GrassGroupingMaximumLevel", "Maximum group level (1 = 2-by-2, 2 = 4-by-4)", 1, 2, 1);
        AddGrassSlider(grouping, "GrassGroupingHysteresisMeters", "Group transition hysteresis (m)", 0, 128, 1);
        AddGrassSlider(grouping, "MaximumGrassSourcesPerGroup", "Maximum source roots per group", 256, 100000, 256);
        AddGrassToggle(grouping, "CacheGrassPresentation", "Reuse unchanged grass visibility and presentation work");
        grouping.AddChild(new Label { Text = "Presentation caching saves CPU work when camera and grass inputs stay unchanged. It preserves the grass appearance; movement and changing inputs still update normally.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        _grassGroupingStatus = new Label { Name = "GrassGroupingRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        grouping.AddChild(_grassGroupingStatus);
        var detail = AddGrassGroup(tab, "Grass detail", "Fewer near blade segments reduce detail. Meadow tufts retain a fuller distant silhouette. The one-ribbon option applies to legacy tufts and requires full camera facing before the near detail transition; other facing settings retain crossed ribbons. Simpler intermediate lighting is off by default.");
        AddGrassToggle(detail, "UseReducedNearGrassGeometry", "Use fewer near blade segments");
        AddGrassToggle(detail, "UseSingleRibbonIntermediateGrass", "Use one distant ribbon for legacy tufts");
        AddGrassToggle(detail, "UseSimplifiedIntermediateGrassShading", "Use simpler intermediate grass lighting");
        var generation = AddGrassGroup(tab, "GPU generation prototype", "Compare CPU and GPU-assisted generation on nearby grass tiles. Live grass stays in place during the test."); AddGrassSlider(generation, "GpuGenerationPrototypeTileCount", "Tiles to sample", 1, 4, 1); _grassGenerationPrototypeRun = new Button { Name = "GrassGpuGenerationPrototypeRun", Text = "Run GPU generation prototype", Disabled = !GrassAvailable }; _grassGenerationPrototypeRun.Pressed += () => StartGrassGpuGenerationPrototype(); generation.AddChild(_grassGenerationPrototypeRun); _grassGenerationPrototypeStatus = new Label { Name = "GrassGpuGenerationPrototypeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; generation.AddChild(_grassGenerationPrototypeStatus);
        var meadow = AddGrassGroup(tab, "Meadow tufts", "Short, irregular tufts. Adjust height, spread, and shape; biome height multipliers also apply.");
        AddGrassDebouncedToggle(meadow, "EnableMeadowTufts", "Use short meadow tufts");
        AddGrassSlider(meadow, "GrassMinimumHeightMeters", "Minimum grass height (m)", .03, 1.99, .01);
        AddGrassSlider(meadow, "GrassMaximumHeightMeters", "Maximum grass height (m)", .04, 2, .01);
        AddGrassSlider(meadow, "MeadowTuftSpread", "Tuft spread", .25, 2, .01);
        AddGrassTuftVariant(meadow);
        var coverage = AddGrassGroup(tab, "Coverage and distance", "Higher grass density places roots closer together for fuller coverage. Biome and surface filters and resident limits can reduce coverage. Density changes rebuild placement after adjustment."); AddGrassToggle(coverage, "Enabled", "Enable standing grass"); AddGrassSlider(coverage, "BladesPerSquareMeter", "Grass density", 1, 96, 1); AddGrassSlider(coverage, "VisibilityRadiusMeters", "Streaming range (m)", 16, 350, 1); AddGrassToggle(coverage, "ClampGrassStreamingToDrawDistance", "Limit streaming to the effective draw distance"); AddGrassSlider(coverage, "GrassStreamingMarginMeters", "Streaming margin beyond draw distance (m)", 0, 64, .5); AddGrassSlider(coverage, "GrassStreamingHysteresisMeters", "Streaming hysteresis (m, 0 = auto)", 0, 32, .5); AddGrassSlider(coverage, "LodTransitionWidthMeters", "LOD transition width (m)", 1, 64, 1); AddGrassSlider(coverage, "LodFarDensity", "Legacy far density ratio", .05, 1, .01);
        var coverageLod = AddGrassGroup(tab, "Coverage LOD", "Near roots refine into deterministic mid tufts and far clusters. Terrain coverage extends the grass appearance without additional roots."); AddGrassToggle(coverageLod, "EnableHierarchicalCoverage", "Enable hierarchical coverage"); AddGrassSlider(coverageLod, "LodNearDistanceMeters", "Near detail distance (m)", 8, 350, 1); AddGrassSlider(coverageLod, "LodMidDistanceMeters", "Mid tuft distance (m)", 32, 750, 1); AddGrassSlider(coverageLod, "LodFarDistanceMeters", "Far geometry distance (m)", 32, 1500, 1); AddGrassSlider(coverageLod, "LodMidSourceDensity", "Mid source density", .05, 1, .01); AddGrassSlider(coverageLod, "LodFarSourceDensity", "Far source density", .01, .5, .01); AddGrassToggle(coverageLod, "EnableTerrainGrassCoverage", "Enable terrain grass coverage"); AddGrassSlider(coverageLod, "TerrainGrassCoverageStartMeters", "Terrain coverage start (m)", 32, 2000, 1); AddGrassSlider(coverageLod, "TerrainGrassCoverageEndMeters", "Terrain coverage end (m)", 64, 4000, 1); AddGrassSlider(coverageLod, "TerrainGrassCoverageStrength", "Terrain coverage strength", 0, 1, .01);
        var movement = AddGrassGroup(tab, "Movement", "A short trail bends grass away from the player and recovers over time."); AddGrassToggle(movement, "EnableGrassInteraction", "Enable grass movement"); AddGrassSlider(movement, "GrassInteractionRadiusMeters", "Movement radius (m)", .2, 5, .05); AddGrassSlider(movement, "GrassInteractionStrength", "Movement strength", 0, 2, .01); AddGrassSlider(movement, "GrassInteractionRecoverySeconds", "Recovery time (s)", .1, 20, .1); AddGrassSlider(movement, "GrassMaximumWidthScale", "Far blade width", 1, 2, .01);
        var cameraFacing = AddGrassGroup(tab, "Distant blades", "Camera facing is on by gameplay default at full strength, completing before the near LOD transition. Untouched distances follow your LOD settings. Disable or tune it here; settings that do not fully face intermediate grass before the handoff retain crossed ribbons."); AddGrassToggle(cameraFacing, "EnableGrassCameraFacing", "Enable camera-facing distant blades"); AddGrassSlider(cameraFacing, "GrassCameraFacingStrength", "Camera-facing strength", 0, 1, .01); AddGrassSlider(cameraFacing, "GrassCameraFacingStartMeters", "Camera-facing start (m)", 0, 1500, 1); AddGrassSlider(cameraFacing, "GrassCameraFacingEndMeters", "Camera-facing end (m)", 1, 2000, 1);
        BuildGrassAppearanceControls(tab);
        var debug = AddGrassGroup(tab, "Debug", "Grass rendering views."); AddGrassDebugMode(debug); AddGrassToggle(debug, "ShowGrassTileBounds", "Show grass tile bounds");
        var cache = AddGrassGroup(tab, "Surface cache", "Keeps only authoritative regular-cell locations, then reconstructs exact triangles per blade. It is shared by active workers and strictly capped using conservative retained-memory accounting; 0 disables it."); AddGrassSlider(cache, "SurfaceCacheMegabytes", "Cache cap (MiB)", 0, 16, .25);
        var budgets = AddGrassGroup(tab, "Streaming budgets", "Worker and upload limits take effect after the slider settles, avoiding repeated scheduler churn while dragging."); AddGrassSlider(budgets, "MaximumInstancesPerTile", "Blade-equivalent cap per tile", 100, 100000, 100); AddGrassSlider(budgets, "MaximumInstances", "Total blade-equivalent cap", 1000, 5000000, 1000); AddGrassSlider(budgets, "MaximumResidentTiles", "Resident tile budget", 16, 1024, 1); AddGrassSlider(budgets, "MaximumTileBuildsInFlight", "Worker builds in flight", 1, 8, 1); AddGrassSlider(budgets, "MaximumTileUploadsPerFrame", "Uploads per frame", 1, 16, 1); AddGrassSlider(budgets, "MaximumTileUploadMilliseconds", "Upload budget (ms)", .1, 20, .1);
        AddGrassSlider(budgets, "MaximumGpuCullingBatchesPerFrame", "GPU culling batches/frame", 16, 1024, 1);
        _grassStatus = new Label { Name = "GrassRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; tab.AddChild(_grassStatus); var row = new HBoxContainer(); var save = new Button { Name = "GrassSaveNow", Text = "Save grass settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; save.Pressed += () => FlushGrassDefaults(); row.AddChild(save); var reset = new Button { Name = "GrassResetDefaults", Text = "Reset to current quality profile", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; reset.Pressed += ResetGrassSettings; row.AddChild(reset); tab.AddChild(row); _grassSaveStatus = new Label { Name = "GrassDefaultsStatus", Modulate = RingUiStyle.Muted }; tab.AddChild(_grassSaveStatus); RefreshGrassControls();
    }
    private static VBoxContainer AddGrassGroup(VBoxContainer p, string title, string note) => TuningMenuUi.CreateSection(p, title, note);
    private void AddGrassSlider(VBoxContainer p, string key, string label, double min, double max, double step) { p.AddChild(new Label { Text = label }); _grassControlKeys.Add(key); var row = new HBoxContainer(); double initial = GrassNumber(key, min); var slider = new HSlider { Name = key, MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Editable = GrassAvailable }; var entry = new SpinBox { Name = key + "Entry", MinValue = min, MaxValue = max, Step = step, Value = initial, CustomMinimumSize = new Vector2(82, 0), Editable = GrassAvailable }; slider.ValueChanged += v => { entry.SetValueNoSignal(v); QueueGrassValue(key, v); }; entry.ValueChanged += v => { _pendingGrassValues.Remove(key); slider.SetValueNoSignal(v); SetGrassTuningValue(key, v); }; row.AddChild(slider); row.AddChild(entry); p.AddChild(row); }
    private void AddGrassToggle(VBoxContainer p, string key, string label) { _grassControlKeys.Add(key); var c = new CheckButton { Name = key, Text = label, ButtonPressed = GrassValue(key).AsBool(), Disabled = !GrassAvailable }; c.Toggled += v => SetGrassTuningValue(key, v); p.AddChild(c); }
    private void AddGrassDebouncedToggle(VBoxContainer p, string key, string label) { _grassControlKeys.Add(key); var c = new CheckButton { Name = key, Text = label, ButtonPressed = GrassValue(key).AsBool(), Disabled = !GrassAvailable }; c.Toggled += v => QueueGrassValue(key, v); p.AddChild(c); }
    private void AddGrassRenderStyle(VBoxContainer parent)
    {
        parent.AddChild(new Label { Text = "Grass render style" });
        _grassControlKeys.Add("GrassRenderStyle");
        var option = new OptionButton { Name = "GrassRenderStyle", Disabled = !GrassAvailable };
        foreach (string style in new[] { "Blades", "Cards" }) option.AddItem(style);
        option.Selected = (int)GrassNumber("GrassRenderStyle", 0);
        option.ItemSelected += index => SetGrassTuningValue("GrassRenderStyle", index);
        parent.AddChild(option);
    }
    private void AddGrassTuftVariant(VBoxContainer parent)
    {
        parent.AddChild(new Label { Text = "Tuft shape selection" });
        _grassControlKeys.Add("MeadowTuftVariant");
        var option = new OptionButton { Name = "MeadowTuftVariant", Disabled = !GrassAvailable };
        foreach (string variant in new[] { "Mixed natural tufts", "Compact", "Spreading", "Leaning" }) option.AddItem(variant);
        option.Selected = (int)GrassNumber("MeadowTuftVariant", 0);
        option.ItemSelected += index => QueueGrassValue("MeadowTuftVariant", index);
        parent.AddChild(option);
    }
    private void AddGrassDebugMode(VBoxContainer p) { _grassControlKeys.Add("GrassDebugMode"); var option = new OptionButton { Name = "GrassDebugMode", Disabled = !GrassAvailable }; foreach (string mode in new[] { "Off", "LOD", "Density", "Wind", "Interaction", "Tiles" }) option.AddItem(mode); option.Selected = (int)GrassNumber("GrassDebugMode", 0); option.ItemSelected += i => SetGrassTuningValue("GrassDebugMode", i); p.AddChild(option); }
    private void AddGrassModifierPreviewButton(VBoxContainer parent)
    {
        var button = new Button { Name = "PlaceGrassModifierPreview", Text = "Place test zone here", Disabled = !GrassAvailable };
        button.Pressed += () =>
        {
            RingStandingGrassLayer? layer = _grassGraphics?.GrassLayer;
            if (layer == null) return;
            bool previousEnabled = layer.GrassModifierPreviewEnabled;
            layer.GrassModifierPreviewEnabled = true;
            if (layer.PlaceGrassModifierPreviewAtViewer())
                SetGrassTuningValue("GrassModifierPreviewEnabled", true);
            else
                layer.GrassModifierPreviewEnabled = previousEnabled;
        };
        parent.AddChild(button);
    }
    private bool GrassAvailable => _grassGraphics?.GrassLayer != null;
    private Variant GrassValue(string key)
    {
        if (key is "EnableTerrainGrassCoverage" or "TerrainGrassCoverageStartMeters" or "TerrainGrassCoverageEndMeters" or "TerrainGrassCoverageStrength") return _grassDefaults.GetValue(key, default);
        return _grassGraphics?.GrassLayer?.Get(key) ?? default;
    }
    private double GrassNumber(string key, double fallback) { Variant v = GrassValue(key); return v.VariantType is Variant.Type.Float or Variant.Type.Int ? v.AsDouble() : fallback; }
    private void QueueGrassValue(string key, Variant value) { _pendingGrassValues[key] = value; _grassApplyDelay = .18; }
    private readonly System.Collections.Generic.Dictionary<string, Variant> _pendingGrassValues = new(); private double _grassApplyDelay;
    private void CommitQueuedGrassValues()
    {
        if (_grassApplyDelay > 0 || _pendingGrassValues.Count == 0) return;
        foreach (var pair in _pendingGrassValues) SetGrassTuningValue(pair.Key, pair.Value);
        _pendingGrassValues.Clear();
    }
    public void SetGrassTuningValue(string property, Variant value) { if (!_grassDefaults.SetOverride(_grassGraphics, property, value)) return; _grassSaveDelay = .5; _grassSaveRetryDelay = 0; RefreshGrassControls(); }
    public bool FlushGrassDefaults() { _grassApplyDelay = 0; CommitQueuedGrassValues(); bool saved = _grassDefaults.Save(); UpdateGrassStatus(); return saved; }
    public void InstallGrassDefaults(string isolatedPath) { if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return; _grassDefaultsPathOverride = isolatedPath; if (_grassDefaults is null || !_grassDefaults.Initialized) return; _grassDefaults = new GameplayGrassDefaults(isolatedPath); _grassDefaults.Initialize(_grassGraphics); RefreshGrassControls(); }
    public Godot.Collections.Dictionary GetGrassTuningDiagnostics() => new() { ["path"] = _grassDefaults.GetPath(), ["dirty"] = _grassDefaults.Dirty, ["last_error"] = _grassDefaults.LastError, ["override_count"] = _grassDefaults.Settings.Overrides.Count, ["available"] = GrassAvailable };
    /// <summary>Restores grass profile values while preserving wind response owned by the Wind tab.</summary>
    public void ResetGrassSettings() { _grassApplyDelay = 0; CommitQueuedGrassValues(); _grassDefaults.Reset(_grassGraphics); _grassSaveDelay = 0; RefreshGrassControls(); FlushGrassDefaults(); }
    /// <summary>Diagnostic UI/test hook; returns false when no immutable resident tile snapshot is available or a run is active.</summary>
    public bool StartGrassGpuGenerationPrototype()
    {
        _grassApplyDelay = 0;
        CommitQueuedGrassValues();
        bool started = _grassGraphics?.GrassLayer?.StartGpuGenerationPrototype() ?? false;
        RefreshGrassControls();
        return started;
    }
    private void RefreshGrassControls() { if (_drawer == null) return; RefreshGrassControlsRecursive(_drawer); UpdateGrassStatus(); }
    private void RefreshGrassControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString(); string key = name.EndsWith("Entry", StringComparison.Ordinal) ? name[..^5] : name;
            if (_grassControlKeys.Contains(key) && !_pendingGrassValues.ContainsKey(key))
            {
                Variant live = GrassValue(key);
                if (child is HSlider slider && live.VariantType is Variant.Type.Float or Variant.Type.Int) { slider.SetValueNoSignal(live.AsDouble()); slider.Editable = GrassAvailable; }
                else if (child is SpinBox entry && live.VariantType is Variant.Type.Float or Variant.Type.Int) { entry.SetValueNoSignal(live.AsDouble()); entry.Editable = GrassAvailable; }
                else if (child is CheckButton toggle && live.VariantType == Variant.Type.Bool) { toggle.SetPressedNoSignal(live.AsBool()); toggle.Disabled = !GrassAvailable; }
                else if (child is ColorPickerButton picker && live.VariantType == Variant.Type.Color) { picker.Color = live.AsColor(); picker.Disabled = !GrassAvailable; }
                else if (child is OptionButton option && live.VariantType is Variant.Type.Float or Variant.Type.Int) { option.Select(Mathf.Clamp((int)live.AsDouble(), 0, option.ItemCount - 1)); option.Disabled = !GrassAvailable; }
            }
            RefreshGrassControlsRecursive(child);
        }
    }
    private void UpdateGrassGroupingStatus()
    {
        if (_grassGroupingStatus == null) return;
        RingStandingGrassLayer? layer = _grassGraphics?.GrassLayer;
        if (layer == null) { _grassGroupingStatus.Text = "Standing grass is unavailable in this scene."; return; }
        float margin = Math.Max(4f, layer.GrassGroupingHysteresisMeters);
        // Cards has no distance LOD: the effective start is clamped beyond the density ladder and the geometry ends at the reach.
        bool cardGroups = layer.IsCardStyle;
        float nearLimit = cardGroups ? 0f : Math.Min(layer.LodNearDistanceMeters, layer.EffectiveGrassDrawDistance() - .01f) + layer.LodTransitionWidthMeters * .5f + margin;
        float firstStart = Math.Max(cardGroups ? layer.EffectiveGrassGroupingStartMeters : layer.GrassGroupingStartMeters, nearLimit) + margin;
        float secondStart = Math.Max(cardGroups ? layer.EffectiveGrassGroupingFarStartMeters : layer.GrassGroupingFarStartMeters, nearLimit) + margin;
        float farLimit = layer.EffectiveGrassDrawDistance() + (cardGroups ? 0f : layer.LodTransitionWidthMeters * .5f);
        string counts = $"2-by-2: {layer.AdaptiveGrassLevel1GroupCount:N0}; 4-by-4: {layer.AdaptiveGrassLevel2GroupCount:N0}. Maximum level: {layer.GrassGroupingMaximumLevel}.";
        string grouping;
        bool inactive = !layer.Enabled || layer.StreamingSuspended || !layer.IsVisibleInTree();
        if (inactive)
            grouping = $"Grouping {(layer.EnableAdaptiveGrassGrouping ? "on" : "off")}. "
                + (!layer.Enabled ? "Grass disabled." : layer.StreamingSuspended ? "Grass streaming suspended." : "Grass hidden.");
        else if (!layer.EnableAdaptiveGrassGrouping)
            grouping = layer.AdaptiveGrassGroupCount == 0 ? "Grouping off. Grass uses individual tiles."
                : $"Grouping off. {layer.AdaptiveGrassGroupCount:N0} groups remain while fine outputs finish restoring.";
        else if (firstStart >= farLimit)
            grouping = $"Grouping on, but complete groups must be beyond {firstStart:F0} m; geometry ends around {farLimit:F0} m. Reduce the group start and near transition distances, or extend coverage. Coordinated handoff also requires a sufficient grass coverage budget.";
        else
            grouping = $"Grouping on. {layer.AdaptiveGrassGroupingStatus}. Complete groups must be beyond {firstStart:F0} m"
                + (layer.GrassGroupingMaximumLevel >= 2 ? $" (4-by-4: {secondStart:F0} m)." : ".")
                + (layer.AdaptiveGrassGroupCount == 0 ? " Compatible distant groups may be unavailable or still warming up." : "");
        string caching = inactive
            ? $"Presentation cache {(layer.CacheGrassPresentation ? "on" : "off")}; grass work is inactive."
            : layer.CacheGrassPresentation
                ? $"Presentation cache on. Total reused scans: visibility {layer.GrassVisibilityCacheHits:N0}, presentation {layer.GrassPresentationCacheHits:N0}, GPU requests {layer.GrassGpuRequestCacheHits:N0}."
                : "Presentation cache off. Grass updates recompute visibility and presentation; reuse totals retain earlier activity.";
        _grassGroupingStatus.Text = grouping + "\n" + counts + "\n" + caching;
    }
    private void UpdateGrassStatus() { UpdateGrassGroupingStatus(); var layer = _grassGraphics?.GrassLayer; if (_grassStatus != null) { if (layer == null) _grassStatus.Text = "Standing grass is unavailable in this scene."; else { var cache = layer.SurfaceCacheDiagnostics; string coverage = $" Coverage: near {layer.LodNearDistanceMeters:F0} m, mid {layer.LodMidDistanceMeters:F0} m, configured far {layer.LodFarDistanceMeters:F0} m; effective draw distance {layer.EffectiveGrassDrawDistance():F0} m; streaming coverage {layer.GrassStreamingCoverageRadiusMeters:F0} m (presentation {layer.GrassStreamingPresentationRadiusMeters:F0} m)."; if (layer.EnableCoordinatedGrassHandoff && layer.EnableGrassAppearance) coverage += " Coordinated handoff limits draw distance to the supported grass coverage budget."; _grassStatus.Text = $"Profile: {_grassGraphics!.CurrentPresetName}. Residents: {layer.ResidentGrassTileCount}/{layer.MaximumResidentTiles} tiles, {layer.ResidentGrassInstanceCount:N0}/{layer.MaximumGrassSourceInstances:N0} sources ({layer.GrassBladesPerSource} blades/source; {(long)layer.ResidentGrassInstanceCount * layer.GrassBladesPerSource:N0}/{(long)layer.MaximumGrassSourceInstances * layer.GrassBladesPerSource:N0} near-detail blade capacity).{coverage} Tile rejection: {layer.GrassFrustumRejectedTileCount:N0} outside view, {layer.GrassDistanceRejectedTileCount:N0} beyond range. Surface cache: {cache["entries"].AsInt64():N0} entries, {cache["estimated_bytes"].AsInt64() / 1024.0:F1}/{cache["byte_cap"].AsInt64() / 1024.0:F1} KiB, {cache["hits"].AsInt64():N0} hits, {cache["misses"].AsInt64():N0} misses, {cache["evictions"].AsInt64():N0} evictions. Groups: {layer.AdaptiveGrassGroupCount:N0} active ({layer.AdaptiveGrassLevel1GroupCount:N0} at 2-by-2, {layer.AdaptiveGrassLevel2GroupCount:N0} at 4-by-4); {layer.AdaptiveGrassGroupedSourceCount:N0} grouped sources. Grouping: {layer.AdaptiveGrassGroupingStatus}. Visibility: {layer.AdaptiveGrassVisibilityCheckCount:N0} group checks, {layer.GrassVisibilityScans:N0} scans/{layer.GrassVisibilityCacheHits:N0} reused. Presentation: {layer.GrassPresentationScans:N0} scans/{layer.GrassPresentationCacheHits:N0} reused. GPU requests: {layer.GrassGpuRequestScans:N0} scans/{layer.GrassGpuRequestCacheHits:N0} reused. GPU: {(layer.UseGpuCulling ? "requested" : "off")}, {layer.GpuCullingStatus}, {layer.GpuCullingBatchCount} batches, {layer.GpuCullingBytes:N0} estimated buffer bytes ({layer.GpuCullingSharedSourceCount} shared sources: {layer.GpuCullingSharedSourceBytes:N0}; outputs: {layer.GpuCullingOutputBytes:N0}). Pending: {layer.PendingGrassTileBuildCount} builds, {layer.PendingGrassUploadCount} uploads."; } } if (_grassGenerationPrototypeRun != null) _grassGenerationPrototypeRun.Disabled = layer == null || layer.GpuGenerationPrototypeRunning; if (_grassGenerationPrototypeStatus != null) { if (layer == null) _grassGenerationPrototypeStatus.Text = "Standing grass is unavailable in this scene."; else { var results = layer.CaptureGpuGenerationPrototypeResults(); var diagnostics = layer.CaptureGpuGenerationPrototypeDiagnostics(); _grassGenerationPrototypeStatus.Text = layer.GpuGenerationPrototypeStatus + (results.Length == 0 ? "" : $" Queries: {results.Sum(result => result.Queries):N0}; columns: {results.Sum(result => result.UniqueColumns):N0}; CPU warm: {results.Sum(result => result.CpuWarmBaselineMilliseconds):F2} ms; GPU hybrid: {results.Sum(result => result.HybridMilliseconds) + diagnostics["session_initialization_ms"].AsDouble() + diagnostics["session_teardown_ms"].AsDouble():F2} ms; cached CPU: {results.Sum(result => result.CachedCpuHybridMilliseconds):F2} ms. Validation total: {diagnostics["run_total_ms"].AsDouble():F2} ms."); } } if (_grassSaveStatus != null) _grassSaveStatus.Text = _grassDefaults.LastError.Length > 0 ? "Grass settings could not be saved." : _grassDefaults.Dirty ? "Grass settings pending." : "Grass settings saved."; }
}
