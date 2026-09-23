using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private Node? _graphicsQuality, _waterFft;
    private GameplayWaterDefaults _waterDefaults = null!;
    private Label? _waterSaveStatus, _waterRuntimeStatus;
    private double _waterSaveDelay, _waterSaveRetryDelay, _waterRefreshDelay;
    private readonly System.Collections.Generic.HashSet<string> _waterControlKeys = new();
    private string? _waterDefaultsPathOverride;

    private void InitializeWaterTuning()
    {
        _graphicsQuality = GetNodeOrNull<Node>(GraphicsQualityPath);
        _waterFft = GetNodeOrNull<Node>(WaterFftPath);
        _waterDefaults = new GameplayWaterDefaults(_waterDefaultsPathOverride);
        _waterDefaults.Initialize(_graphicsQuality, _waterFft);
    }
    private void ProcessWaterTuning(double delta)
    {
        if (_open) { _waterRefreshDelay -= delta; if (_waterRefreshDelay <= 0) { _waterRefreshDelay = .25; RefreshWaterControls(); } }
        if (_waterDefaults.Dirty) { _waterSaveDelay -= delta; _waterSaveRetryDelay -= delta; if (_waterSaveDelay <= 0 && _waterSaveRetryDelay <= 0 && !FlushWaterDefaults()) _waterSaveRetryDelay = 5; }
    }
    private void BuildWaterTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Water");
        tab.AddChild(new Label { Text = "Adjust water live. Your choices are saved automatically.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        AddWaterPresets(tab);
        VBoxContainer section = AddWaterGroup(tab, "Adaptive FFT updates", "Small or far water updates more slowly. Nearby water retains the configured rates. With no relevant ocean, simulation pauses after the grace period.");
        AddWaterToggle(section, "FftAdaptiveUpdatesEnabled", "Adapt FFT updates to nearby water");
        AddWaterSlider(section, "FftFullRateAreaSquareKilometers", "Water area for full rate (weighted km²)", .01, 16, .01);
        AddWaterSlider(section, "FftFullRateDistanceMeters", "Full-rate near radius (m)", 25, 1000, 5);
        AddWaterSlider(section, "FftMinimumUpdatesPerSecond", "Low-demand minimum rate (Hz)", 1, 30, 1);
        AddWaterSlider(section, "FftIdleGraceSeconds", "No-ocean idle grace (s)", 0, 5, .1);
        section = AddWaterGroup(tab, "Near ocean FFT surface", "FFT drives nearby ocean shape, chop, foam and crest light. Lakes, rivers, distant water, and unavailable FFT use the analytic fallback below.");
        AddWaterToggle(section, "FftEnabled", "Enable FFT ocean surface");
        AddWaterOption(section, "FftResolution", "FFT resolution", new[] { "128", "256", "512", "1024" }, new[] { 128, 256, 512, 1024 });
        AddWaterOption(section, "FftCascadeCount", "FFT cascades", new[] { "1", "2", "3", "4" }, new[] { 1, 2, 3, 4 });
        AddWaterSlider(section, "FftGeometryUpdatesPerSecond", "Geometry updates / sec", 5, 120, 1);
        AddWaterSlider(section, "FftUpdatesPerSecond", "Detail updates / sec / layer", 5, 120, 1);
        AddWaterSlider(section, "FftGeometryHeightScale", "Geometry height", 0, 10, .01);
        AddWaterSlider(section, "FftGeometryChoppiness", "Geometry choppiness", 0, 3, .01);
        AddWaterSlider(section, "FftGeometryDistanceMeters", "Geometry distance (m)", 25, 2000, 5);
        AddWaterSlider(section, "FftGeometryFilterMeters", "Geometry filter (m)", 1, 8, .25);
        AddWaterSlider(section, "FftCrestLightingStrength", "Crest light transmission", 0, 1, .01);
        section = AddWaterGroup(tab, "FFT shoreline transition", "Terrain contact holds the primary wave shape first. Horizontal chop fades earlier, while fine ripple shading remains available.");
        AddWaterSlider(section, "FftShorelineFadeDistanceMeters", "Terrain wave fade distance (m)", 1, 64, .25);
        AddWaterSlider(section, "FftShorelineChopFadeDistanceMeters", "Shoreline chop fade distance (m)", 1, 64, .25);
        AddWaterSlider(section, "FftShorelineShallowDepthMeters", "Shallow-water anchor depth (m)", .1, 20, .1);
        AddWaterSlider(section, "FftDeepWaterWaveDepthMeters", "Full ocean waves at depth (m)", 5, 500, 1);
        section = AddWaterGroup(tab, "Analytic fallback, lakes and distant water", "Used for lakes, rivers, disabled or unsupported FFT, and the distant ocean path.");
        AddWaterSlider(section, "WaterWaveHeightMeters", "Swell height (m)", 0, 10, .01);
        AddWaterSlider(section, "WaterWaveLengthMeters", "Swell wavelength (m)", 2, 200, 1);
        AddWaterSlider(section, "WaterWaveSpeed", "Swell speed", 0, 10, .05);
        AddWaterSlider(section, "WaterWaveDetailStrength", "Analytic detail", 0, 1, .01);
        AddWaterSlider(section, "WaterWaveDomainWarpStrength", "Domain warp", 0, 1.5, .01);
        AddWaterSlider(section, "WaterWaveCrestSharpness", "Crest sharpness", .05, 4, .05);
        AddWaterSlider(section, "WaterWaveCrestBlend", "Crest blend", 0, 1, .01);
        AddWaterSlider(section, "WaterWaveNormalStrength", "Analytic normal strength", 0, 2, .01);
        AddWaterSlider(section, "WaterWaveBandlimitStartRadians", "Band-limit start (rad/px)", .05, 2, .01);
        AddWaterSlider(section, "WaterWaveBandlimitEndRadians", "Band-limit end (rad/px)", .1, 3, .01);
        section = AddWaterGroup(tab, "Colour and transparency", "RGB Beer-Lambert extinction and shallow in-scattering. Red absorbs first; blue travels deepest.");
        AddWaterSlider(section, "WaterRefractionStrength", "Refraction", 0, .08, .001);
        AddWaterSlider(section, "WaterAbsorptionDensity", "Absorption density", .001, 2, .001);
        AddWaterColor(section, "WaterAbsorptionCoefficients", "RGB absorption (red > green > blue)");
        AddWaterSlider(section, "WaterShallowInscatterStrength", "Shallow luminous scatter", 0, 2, .01);
        AddWaterSlider(section, "WaterCrestTransmissionStrength", "Crest/backlight transmission", 0, 2, .01);
        AddWaterSlider(section, "WaterShallowAlpha", "Shallow alpha", 0, 1, .01);
        AddWaterSlider(section, "WaterDeepAlpha", "Deep alpha", 0, 1, .001);
        AddWaterColor(section, "WaterShallowTint", "Shallow tint");
        AddWaterColor(section, "WaterDeepTint", "Deep tint");
        AddWaterColor(section, "WaterFoamColor", "Foam colour");
        section = AddWaterGroup(tab, "Foam", "Shore foam uses depth. Near-ocean whitecaps come from FFT compression; analytic crest foam remains for fallback water.");
        AddWaterSlider(section, "WaterShoreFoamDepthMeters", "Shore foam depth (m)", .1, 20, .1);
        AddWaterSlider(section, "WaterShoreFoamStrength", "Shore foam strength", 0, 2, .01);
        AddWaterSlider(section, "WaterBreakingFoamStrength", "Crest foam strength", 0, 2, .01);
        AddWaterSlider(section, "WaterBreakingFoamSlopeStart", "Crest foam start", .05, 1.5, .01);
        AddWaterSlider(section, "WaterBreakingFoamSlopeEnd", "Crest foam end", .1, 2, .01);
        section = AddWaterGroup(tab, "Distant variation and filtering", "Foam patchiness adds irregular groups of foam. Higher fine-detail filtering smooths distant ripples and highlights.");
        AddWaterSlider(section, "WaterSwellVariation", "Distant swell variation", 0, 1, .01);
        AddWaterSlider(section, "WaterFoamPatchiness", "Foam patchiness", 0, 1, .01);
        AddWaterSlider(section, "WaterDistantFoamVisibility", "Distant foam visibility", 0, 1, .01);
        AddWaterSlider(section, "WaterDetailFilteringStrength", "Fine detail filtering", 0, 2, .01);
        section = AddWaterGroup(tab, "Distance and reflection", "Fade fine analytic detail with distance to protect frame time.");
        AddWaterSlider(section, "WaterWaveGeometryFadeMeters", "Geometry detail distance (m)", 100, 50000, 100);
        AddWaterSlider(section, "WaterWaveNormalFadeStartMeters", "Normal fade start (m)", 100, 50000, 100);
        AddWaterSlider(section, "WaterWaveNormalFadeEndMeters", "Normal fade end (m)", 200, 100000, 100);
        AddWaterSlider(section, "WaterFarRoughness", "Far roughness", .05, 1, .01);
        AddWaterSlider(section, "WaterFresnelF0", "Fresnel base reflectance", 0, .12, .001);
        AddWaterSlider(section, "WaterFresnelStrength", "Fresnel strength", 0, 1.5, .01);
        AddWaterSlider(section, "WaterSpecularAaStrength", "Specular AA", 0, 2, .01);
        section = AddWaterGroup(tab, "Water-only screen-space reflections", "Bounded view-space ray tracing. Misses retain the existing environment/grazing reflection; no temporal history is used.");
        AddWaterToggle(section, "WaterSsrEnabled", "Enable water SSR");
        AddWaterSlider(section, "WaterSsrStrength", "SSR strength", 0, 1, .01);
        AddWaterSlider(section, "WaterSsrCoarseSteps", "SSR coarse steps", 1, 32, 1);
        AddWaterSlider(section, "WaterSsrRefinementSteps", "SSR refinement steps", 0, 8, 1);
        AddWaterSlider(section, "WaterSsrMaxRayDistance", "SSR max ray distance (m)", 10, 1000, 5);
        AddWaterSlider(section, "WaterSsrCameraDistance", "SSR camera distance (m)", 10, 1000, 5);
        AddWaterSlider(section, "WaterSsrDepthToleranceNear", "SSR near depth tolerance", .01, 2, .01);
        AddWaterSlider(section, "WaterSsrRayBias", "SSR ray bias", .01, 1, .01);
        AddWaterSlider(section, "WaterSsrDepthToleranceFar", "SSR far depth tolerance", .1, 8, .01);
        AddWaterSlider(section, "WaterSsrEdgeFade", "SSR edge fade", .01, .4, .01);
        AddWaterSlider(section, "WaterSsrRoughnessCutoff", "SSR roughness cutoff", .02, 1, .01);
        AddWaterOption(section, "WaterSsrDebugMode", "SSR debug", new[] { "Off", "Confidence", "Hit mask", "Step ratio" }, new[] { 0, 1, 2, 3 });
        section = AddWaterGroup(tab, "FFT simulation and small ripples", "Cascade zero adds only fine normal detail when cascade one drives the large geometry.");
        AddWaterSlider(section, "FftNormalStrength", "FFT normal strength", 0, 1, .01);
        AddWaterSlider(section, "FftFoamStrength", "FFT foam strength", 0, 1, .01);
        AddWaterSlider(section, "FftFoamAmount", "FFT foam amount", 0, 10, .1);
        AddWaterSlider(section, "FftWhitecap", "FFT whitecap", 0, 2, .01);
        AddWaterSlider(section, "FftWindSpeedMetersPerSecond", "FFT wind speed (m/s)", .1, 60, .1);
        AddWaterSlider(section, "FftWindDirectionDegrees", "FFT wind direction (deg)", -180, 180, 1);
        AddWaterToggle(section, "FftFollowWeatherWind", "Follow weather wind");
        section.AddChild(new Label { Name = "FftWeatherWindNotice", Text = "Manual wind controls are unavailable while following weather wind.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        AddWaterSlider(section, "FftSmallWaveLengthMeters", "Small patch size (m)", 4, 256, 1);
        AddWaterSlider(section, "FftLargeWaveLengthMeters", "Large patch size (m)", 16, 2048, 1);
        AddWaterSlider(section, "FftDetailDistanceMeters", "FFT detail distance (m)", 25, 4000, 5);
        _waterRuntimeStatus = new Label { Name = "WaterRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; tab.AddChild(_waterRuntimeStatus);
        var row = new HBoxContainer(); var save = new Button { Name = "WaterSaveNow", Text = "Save water settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; save.Pressed += () => FlushWaterDefaults(); row.AddChild(save);
        var reset = new Button { Name = "WaterResetDefaults", Text = "Reset water settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; reset.Pressed += ResetWaterSettings; row.AddChild(reset); tab.AddChild(row);
        _waterSaveStatus = new Label { Name = "WaterDefaultsStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; tab.AddChild(_waterSaveStatus); RefreshWaterControls();
    }
    private static VBoxContainer AddWaterGroup(VBoxContainer parent, string title, string note) => TuningMenuUi.CreateSection(parent, title, note);
    private void AddWaterPresets(VBoxContainer parent) { var row = new HBoxContainer(); foreach (string name in new[] { "Simple", "Balanced", "Detailed" }) { var b = new Button { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; b.Pressed += () => ApplyWaterPreset(name); row.AddChild(b); } parent.AddChild(row); }
    private void AddWaterSlider(VBoxContainer p, string key, string label, double min, double max, double step)
    {
        p.AddChild(new Label { Text = label }); _waterControlKeys.Add(key); var row = new HBoxContainer(); double initial = WaterNumber(key, min); var slider = new HSlider { Name = key, MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Editable = WaterTargetAvailable(key) }; var entry = new SpinBox { Name = key + "Entry", MinValue = min, MaxValue = max, Step = step, Value = initial, CustomMinimumSize = new Vector2(82, 0), Editable = WaterTargetAvailable(key) }; slider.ValueChanged += v => { entry.SetValueNoSignal(v); SetWaterTuningValue(key, v); }; entry.ValueChanged += v => { slider.SetValueNoSignal(v); SetWaterTuningValue(key, v); }; row.AddChild(slider); row.AddChild(entry); p.AddChild(row);
    }
    private void AddWaterToggle(VBoxContainer p, string key, string label) { _waterControlKeys.Add(key); var c = new CheckButton { Name = key, Text = label, ButtonPressed = WaterValue(key).AsBool(), Disabled = !WaterTargetAvailable(key) }; c.Toggled += v => SetWaterTuningValue(key, v); p.AddChild(c); }
    private void AddWaterOption(VBoxContainer p, string key, string label, string[] labels, int[] values) { _waterControlKeys.Add(key); var row = new HBoxContainer(); row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) }); var o = new OptionButton { Name = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = !WaterTargetAvailable(key) }; int current = WaterValue(key).AsInt32(); for (int i=0;i<labels.Length;i++) { o.AddItem(labels[i], values[i]); if (values[i] == current) o.Select(i); } o.ItemSelected += index => SetWaterTuningValue(key, o.GetItemId((int)index)); row.AddChild(o); p.AddChild(row); }
    private void AddWaterColor(VBoxContainer p, string key, string label) { _waterControlKeys.Add(key); var row = new HBoxContainer(); row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }); var picker = new ColorPickerButton { Name = key, Color = WaterValue(key).AsColor(), Disabled = !WaterTargetAvailable(key) }; picker.ColorChanged += c => SetWaterTuningValue(key, c); row.AddChild(picker); p.AddChild(row); }
    private Variant WaterValue(string key) { Node? target = key.StartsWith("Fft", StringComparison.Ordinal) ? _waterFft : _graphicsQuality; return target?.Get(key.StartsWith("Fft", StringComparison.Ordinal) ? key[3..] : key) ?? default; }
    private double WaterNumber(string key, double fallback) { Variant value = WaterValue(key); return value.VariantType is Variant.Type.Float or Variant.Type.Int ? value.AsDouble() : fallback; }
    private bool WaterTargetAvailable(string key)
    {
        if (key is "FftWindSpeedMetersPerSecond" or "FftWindDirectionDegrees")
            return _waterFft != null && !WaterValue("FftFollowWeatherWind").AsBool();
        return key.StartsWith("Fft", StringComparison.Ordinal) ? _waterFft != null : _graphicsQuality != null;
    }
    public void SetWaterTuningValue(string property, Variant value)
    {
        if (!_waterDefaults.SetOverride(_graphicsQuality, _waterFft, property, value)) return;
        _waterSaveDelay = .5;
        _waterSaveRetryDelay = 0;
        RefreshWaterControls();
    }
    public bool FlushWaterDefaults()
    {
        bool saved = _waterDefaults.Save();
        UpdateWaterStatus();
        return saved;
    }
    /// <summary>Test fixture hook that keeps smoke-test preferences outside user://.</summary>
    public void InstallWaterDefaults(string isolatedPath)
    {
        string generatedRoot = ProjectSettings.GlobalizePath("user://generated").Replace('\\', '/');
        string normalizedPath = isolatedPath.Replace('\\', '/');
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)
            && !normalizedPath.StartsWith(generatedRoot + "/", StringComparison.Ordinal)) return;

        _waterDefaultsPathOverride = isolatedPath;
        if (_waterDefaults is null || !_waterDefaults.Initialized) return;
        _waterDefaults = new GameplayWaterDefaults(isolatedPath);
        _waterDefaults.Initialize(_graphicsQuality, _waterFft);
        RefreshWaterControls();
    }
    public Godot.Collections.Dictionary GetWaterTuningDiagnostics() => new() { ["path"] = _waterDefaults.GetPath(), ["dirty"] = _waterDefaults.Dirty, ["last_error"] = _waterDefaults.LastError, ["override_count"] = _waterDefaults.Settings.Overrides.Count };
    private void ApplyWaterPreset(string name) { foreach (var pair in name switch { "Simple" => new (string, Variant)[] { ("FftEnabled", false), ("FftGeometryUpdatesPerSecond", 60f), ("FftGeometryHeightScale", 0f), ("FftGeometryChoppiness", 0f), ("FftShorelineFadeDistanceMeters", 10f), ("FftShorelineChopFadeDistanceMeters", 20f), ("FftShorelineShallowDepthMeters", 3f), ("WaterWaveDetailStrength", .25f), ("WaterSpecularAaStrength", .45f), ("WaterSwellVariation", .65f), ("WaterFoamPatchiness", .65f), ("WaterDistantFoamVisibility", .20f), ("WaterDetailFilteringStrength", 1f) }, "Detailed" => new (string, Variant)[] { ("FftEnabled", true), ("FftResolution", 256), ("FftCascadeCount", 2), ("FftUpdatesPerSecond", 30f), ("FftGeometryUpdatesPerSecond", 60f), ("FftDetailDistanceMeters", 1100f), ("FftGeometryHeightScale", 1.15f), ("FftGeometryChoppiness", .9f), ("FftGeometryDistanceMeters", 1100f), ("FftGeometryFilterMeters", 2f), ("FftCrestLightingStrength", .45f), ("FftShorelineFadeDistanceMeters", 10f), ("FftShorelineChopFadeDistanceMeters", 20f), ("FftShorelineShallowDepthMeters", 3f), ("FftSmallWaveLengthMeters", 12f), ("FftLargeWaveLengthMeters", 79f), ("WaterSpecularAaStrength", 1f), ("WaterSwellVariation", .65f), ("WaterFoamPatchiness", .65f), ("WaterDistantFoamVisibility", .20f), ("WaterDetailFilteringStrength", 1f) }, _ => new (string, Variant)[] { ("FftEnabled", true), ("FftResolution", 128), ("FftCascadeCount", 2), ("FftUpdatesPerSecond", 24f), ("FftGeometryUpdatesPerSecond", 60f), ("FftDetailDistanceMeters", 1100f), ("FftGeometryHeightScale", 1f), ("FftGeometryChoppiness", .75f), ("FftGeometryDistanceMeters", 1100f), ("FftGeometryFilterMeters", 2f), ("FftCrestLightingStrength", .35f), ("FftShorelineFadeDistanceMeters", 10f), ("FftShorelineChopFadeDistanceMeters", 20f), ("FftShorelineShallowDepthMeters", 3f), ("FftSmallWaveLengthMeters", 12f), ("FftLargeWaveLengthMeters", 79f), ("WaterSpecularAaStrength", .85f), ("WaterSwellVariation", .65f), ("WaterFoamPatchiness", .65f), ("WaterDistantFoamVisibility", .20f), ("WaterDetailFilteringStrength", 1f) } }) SetWaterTuningValue(pair.Item1, pair.Item2); SetWaterTuningValue("FftDeepWaterWaveDepthMeters", 100f); }
    private void ResetWaterSettings() { _waterDefaults.Reset(_graphicsQuality, _waterFft); _waterSaveDelay = 0; RefreshWaterControls(); FlushWaterDefaults(); }
    private void RefreshWaterControls()
    {
        if (_drawer == null) return;
        RefreshWaterControlsRecursive(_drawer);
        UpdateWaterStatus();
    }
    private void RefreshWaterControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.EndsWith("Entry", StringComparison.Ordinal) ? name[..^5] : name;
            if (_waterControlKeys.Contains(key))
            {
                Variant live = WaterValue(key);
                bool available = WaterTargetAvailable(key);
                if (child is HSlider slider && live.VariantType is Variant.Type.Float or Variant.Type.Int)
                {
                    slider.SetValueNoSignal(live.AsDouble()); slider.Editable = available;
                }
                else if (child is SpinBox entry && live.VariantType is Variant.Type.Float or Variant.Type.Int)
                {
                    entry.SetValueNoSignal(live.AsDouble()); entry.Editable = available;
                }
                else if (child is CheckButton toggle && live.VariantType == Variant.Type.Bool)
                    toggle.SetPressedNoSignal(live.AsBool());
                else if (child is ColorPickerButton picker && live.VariantType == Variant.Type.Color)
                    picker.Color = live.AsColor();
                else if (child is OptionButton options && live.VariantType is Variant.Type.Int or Variant.Type.Float)
                {
                    options.Disabled = !available;
                    for (int i = 0; i < options.ItemCount; i++)
                        if (options.GetItemId(i) == live.AsInt32()) options.Select(i);
                }
            }
            if (child.Name == "FftWeatherWindNotice" && child is Control notice)
                notice.Visible = WaterValue("FftFollowWeatherWind").AsBool();
            RefreshWaterControlsRecursive(child);
        }
    }
    private void UpdateWaterStatus()
    {
        if (_waterRuntimeStatus != null)
        {
            if (_waterFft == null)
            {
                _waterRuntimeStatus.Text = "Fine FFT waves are unavailable here; analytic swell remains active.";
            }
            else
            {
                int activeResolution = _waterFft.Get("ActiveResolution").AsInt32();
                int activeCascades = _waterFft.Get("ActiveCascadeCount").AsInt32();
                float requestedUpdates = _waterFft.Get("UpdatesPerSecond").AsSingle();
                float requestedGeometryUpdates = _waterFft.Get("GeometryUpdatesPerSecond").AsSingle();
                float actualGeometryUpdates = _waterFft.Get("ActualGeometryUpdatesPerSecond").AsSingle();
                float actualDetailUpdates = _waterFft.Get("ActualDetailUpdatesPerSecond").AsSingle();
                float effectiveGeometryDistance = _waterFft.Get("EffectiveGeometryDistanceMeters").AsSingle();
                float requestedGeometryDistance = _waterFft.Get("GeometryDistanceMeters").AsSingle();
                int relevantSectors = _waterFft.Get("RelevantWaterSectorCount").AsInt32();
                int relevantCells = _waterFft.Get("RelevantWaterCellCount").AsInt32();
                double relevantArea = _waterFft.Get("RelevantWaterAreaSquareKilometers").AsDouble();
                float nearestDistance = _waterFft.Get("NearestRelevantWaterDistanceMeters").AsSingle();
                float targetGeometryUpdates = _waterFft.Get("TargetGeometryUpdatesPerSecond").AsSingle();
                float targetDetailUpdates = _waterFft.Get("TargetDetailUpdatesPerSecond").AsSingle();
                string demandStatus = _waterFft.Get("WaterDemandStatus").AsString();
                double demandCheckMilliseconds = _waterFft.Get("WaterDemandCheckMilliseconds").AsDouble();
                long completedUpdates = _waterFft.Get("CompletedFftUpdates").AsInt64();
                string nearestWater = float.IsFinite(nearestDistance) ? $"{nearestDistance:0} m" : "none";
                _waterRuntimeStatus.Text = "Ocean surface: " + _waterFft.Get("Status").AsString()
                    + $"\nActive FFT: {activeResolution}px, {activeCascades} cascades. Geometry {actualGeometryUpdates:0.#}/{requestedGeometryUpdates:0.#} Hz; detail {actualDetailUpdates:0.#}/{requestedUpdates:0.#} Hz per layer."
                    + $"\nDemand: {demandStatus}; relevant {relevantSectors} chunks / {relevantCells} cells, weighted area {relevantArea:0.###} km², nearest {nearestWater}."
                    + $"\nAdaptive targets: geometry {targetGeometryUpdates:0.#}/{requestedGeometryUpdates:0.#} Hz; detail {targetDetailUpdates:0.#}/{requestedUpdates:0.#} Hz. Actual rates above; demand check {demandCheckMilliseconds:0.###} ms; completed FFT updates {completedUpdates}."
                    + $"\nFFT geometry range: {effectiveGeometryDistance:0} m effective (requested {requestedGeometryDistance:0} m).";
            }
        }
        if (_waterSaveStatus != null)
            _waterSaveStatus.Text = _waterDefaults.LastError.Length > 0
                ? "Water settings could not be saved."
                : _waterDefaults.Dirty ? "Water settings pending." : "Water settings saved.";
    }
}
