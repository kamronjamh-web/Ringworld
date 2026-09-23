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
        var gpuCulling = AddGrassGroup(tab, "GPU culling", "GPU compaction keeps the canonical CPU placement buffer. Unsupported renderers retain CPU and vertex-LOD rendering."); AddGrassToggle(gpuCulling, "UseGpuCulling", "Use GPU grass culling"); AddGrassToggle(gpuCulling, "GpuFrustumCulling", "Cull grass outside the camera frustum"); AddGrassToggle(gpuCulling, "SkipUnchangedGpuCulling", "Skip unchanged GPU culling updates"); AddGrassToggle(gpuCulling, "GroupGrassComputePhases", "Group grass compute phases"); AddGrassToggle(gpuCulling, "CullGrassTiles", "Skip invisible grass tiles and unused LODs"); AddGrassDebouncedToggle(gpuCulling, "UseGrassClumps", "Use four-blade grass clumps");
        var generation = AddGrassGroup(tab, "GPU generation prototype", "Compare CPU and GPU-assisted generation on nearby grass tiles. Live grass stays in place during the test."); AddGrassSlider(generation, "GpuGenerationPrototypeTileCount", "Tiles to sample", 1, 4, 1); _grassGenerationPrototypeRun = new Button { Name = "GrassGpuGenerationPrototypeRun", Text = "Run GPU generation prototype", Disabled = !GrassAvailable }; _grassGenerationPrototypeRun.Pressed += () => StartGrassGpuGenerationPrototype(); generation.AddChild(_grassGenerationPrototypeRun); _grassGenerationPrototypeStatus = new Label { Name = "GrassGpuGenerationPrototypeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; generation.AddChild(_grassGenerationPrototypeStatus);
        var coverage = AddGrassGroup(tab, "Coverage and distance", "Target density is filtered by biome and surface eligibility. Density changes rebuild grass placement after you finish adjusting it."); AddGrassToggle(coverage, "Enabled", "Enable standing grass"); AddGrassSlider(coverage, "BladesPerSquareMeter", "Target near density (blades/m²)", 1, 96, 1); AddGrassSlider(coverage, "VisibilityRadiusMeters", "Streaming range (m)", 16, 350, 1); AddGrassSlider(coverage, "LodTransitionWidthMeters", "LOD transition width (m)", 1, 64, 1); AddGrassSlider(coverage, "LodFarDensity", "Legacy far density ratio", .05, 1, .01);
        var coverageLod = AddGrassGroup(tab, "Coverage LOD", "Near roots refine into deterministic mid tufts and far clusters. Terrain coverage extends the grass appearance without additional roots."); AddGrassToggle(coverageLod, "EnableHierarchicalCoverage", "Enable hierarchical coverage"); AddGrassSlider(coverageLod, "LodNearDistanceMeters", "Near detail distance (m)", 8, 350, 1); AddGrassSlider(coverageLod, "LodMidDistanceMeters", "Mid tuft distance (m)", 32, 750, 1); AddGrassSlider(coverageLod, "LodFarDistanceMeters", "Far geometry distance (m)", 32, 1500, 1); AddGrassSlider(coverageLod, "LodMidSourceDensity", "Mid source density", .05, 1, .01); AddGrassSlider(coverageLod, "LodFarSourceDensity", "Far source density", .01, .5, .01); AddGrassToggle(coverageLod, "EnableTerrainGrassCoverage", "Enable terrain grass coverage"); AddGrassSlider(coverageLod, "TerrainGrassCoverageStartMeters", "Terrain coverage start (m)", 32, 2000, 1); AddGrassSlider(coverageLod, "TerrainGrassCoverageEndMeters", "Terrain coverage end (m)", 64, 4000, 1); AddGrassSlider(coverageLod, "TerrainGrassCoverageStrength", "Terrain coverage strength", 0, 1, .01);
        var movement = AddGrassGroup(tab, "Movement", "A short trail bends grass away from the player and recovers over time."); AddGrassToggle(movement, "EnableGrassInteraction", "Enable grass movement"); AddGrassSlider(movement, "GrassInteractionRadiusMeters", "Movement radius (m)", .2, 5, .05); AddGrassSlider(movement, "GrassInteractionStrength", "Movement strength", 0, 2, .01); AddGrassSlider(movement, "GrassInteractionRecoverySeconds", "Recovery time (s)", .1, 20, .1); AddGrassSlider(movement, "GrassWindStrengthMultiplier", "Wind strength", 0, 2, .01); AddGrassSlider(movement, "GrassWindGustMultiplier", "Wind gust", 0, 2, .01); AddGrassSlider(movement, "GrassMaximumWidthScale", "Far blade width", 1, 2, .01);
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
    private void AddGrassDebugMode(VBoxContainer p) { _grassControlKeys.Add("GrassDebugMode"); var option = new OptionButton { Name = "GrassDebugMode", Disabled = !GrassAvailable }; foreach (string mode in new[] { "Off", "LOD", "Density", "Wind", "Interaction", "Tiles" }) option.AddItem(mode); option.Selected = (int)GrassNumber("GrassDebugMode", 0); option.ItemSelected += i => SetGrassTuningValue("GrassDebugMode", i); p.AddChild(option); }
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
    /// <summary>Restores the selected hardware/profile grass values and removes every player override.</summary>
    public void ResetGrassSettings() { _pendingGrassValues.Clear(); _grassDefaults.Reset(_grassGraphics); _grassSaveDelay = 0; RefreshGrassControls(); FlushGrassDefaults(); }
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
    private void UpdateGrassStatus() { var layer = _grassGraphics?.GrassLayer; if (_grassStatus != null) { if (layer == null) _grassStatus.Text = "Standing grass is unavailable in this scene."; else { var cache = layer.SurfaceCacheDiagnostics; string coverage = $" Coverage: near {layer.LodNearDistanceMeters:F0} m, mid {layer.LodMidDistanceMeters:F0} m, far {layer.LodFarDistanceMeters:F0} m."; _grassStatus.Text = $"Profile: {_grassGraphics!.CurrentPresetName}. Residents: {layer.ResidentGrassTileCount}/{layer.MaximumResidentTiles} tiles, {layer.ResidentGrassInstanceCount:N0}/{layer.MaximumGrassSourceInstances:N0} sources ({layer.GrassBladesPerSource} blades/source; {layer.ResidentGrassInstanceCount * layer.GrassBladesPerSource:N0}/{layer.MaximumInstances:N0} equivalent blades).{coverage} Surface cache: {cache["entries"].AsInt64():N0} entries, {cache["estimated_bytes"].AsInt64() / 1024.0:F1}/{cache["byte_cap"].AsInt64() / 1024.0:F1} KiB, {cache["hits"].AsInt64():N0} hits, {cache["misses"].AsInt64():N0} misses, {cache["evictions"].AsInt64():N0} evictions. GPU: {(layer.UseGpuCulling ? "requested" : "off")}, {layer.GpuCullingStatus}, {layer.GpuCullingBatchCount} batches, {layer.GpuCullingBytes:N0} estimated buffer bytes ({layer.GpuCullingSharedSourceCount} shared sources: {layer.GpuCullingSharedSourceBytes:N0}; outputs: {layer.GpuCullingOutputBytes:N0}). Pending: {layer.PendingGrassTileBuildCount} builds, {layer.PendingGrassUploadCount} uploads."; } } if (_grassGenerationPrototypeRun != null) _grassGenerationPrototypeRun.Disabled = layer == null || layer.GpuGenerationPrototypeRunning; if (_grassGenerationPrototypeStatus != null) { if (layer == null) _grassGenerationPrototypeStatus.Text = "Standing grass is unavailable in this scene."; else { var results = layer.CaptureGpuGenerationPrototypeResults(); var diagnostics = layer.CaptureGpuGenerationPrototypeDiagnostics(); _grassGenerationPrototypeStatus.Text = layer.GpuGenerationPrototypeStatus + (results.Length == 0 ? "" : $" Queries: {results.Sum(result => result.Queries):N0}; columns: {results.Sum(result => result.UniqueColumns):N0}; CPU warm: {results.Sum(result => result.CpuWarmBaselineMilliseconds):F2} ms; GPU hybrid: {results.Sum(result => result.HybridMilliseconds) + diagnostics["session_initialization_ms"].AsDouble() + diagnostics["session_teardown_ms"].AsDouble():F2} ms; cached CPU: {results.Sum(result => result.CachedCpuHybridMilliseconds):F2} ms. Validation total: {diagnostics["run_total_ms"].AsDouble():F2} ms."); } } if (_grassSaveStatus != null) _grassSaveStatus.Text = _grassDefaults.LastError.Length > 0 ? "Grass settings could not be saved." : _grassDefaults.Dirty ? "Grass settings pending." : "Grass settings saved."; }
}
