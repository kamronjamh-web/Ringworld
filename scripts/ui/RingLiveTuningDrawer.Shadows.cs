using Godot;
using RingworldTechDemo.Voxel.LOD;
namespace RingworldTechDemo.UI;
public partial class RingLiveTuningDrawer
{
    private RingGraphicsQualityController? _shadowGraphics;
    private GameplayShadowDefaults _shadowDefaults = null!;
    private Label? _shadowStatus;
    private double _shadowSaveDelay, _shadowRetryDelay, _shadowRefreshDelay;
    private readonly System.Collections.Generic.Dictionary<string, Range> _shadowRanges = new();
    private readonly System.Collections.Generic.Dictionary<string, Label> _shadowValueLabels = new();
    private readonly System.Collections.Generic.Dictionary<string, OptionButton> _shadowOptions = new();
    private CheckButton? _shadowEnabled;
    private CheckButton? _localShadowHandoffEnabled;
    private CheckButton? _terrainMacroShadowEnabled;
    private CheckButton? _terrainMacroWallShadowsEnabled;
    private CheckButton? _sunStabilizationEnabled;
    private CheckButton? _pauseDayNight;
    private string? _shadowDefaultsPathOverride;
    private void InitializeShadowTuning() { _shadowGraphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath); _shadowDefaults = new GameplayShadowDefaults(_shadowDefaultsPathOverride); _shadowDefaults.Initialize(_shadowGraphics); }
    private void ProcessShadowTuning(double delta) { if (_open) { _shadowRefreshDelay -= delta; if (_shadowRefreshDelay <= 0) { _shadowRefreshDelay=.25; RefreshShadowControls(); } } if (_shadowDefaults?.Dirty != true) return; _shadowSaveDelay -= delta; _shadowRetryDelay -= delta; if (_shadowSaveDelay <= 0 && _shadowRetryDelay <= 0 && !FlushShadowDefaults()) _shadowRetryDelay=5; }
    private bool FlushShadowDefaults() { bool saved = _shadowDefaults?.Save() ?? true; UpdateShadowStatus(); return saved; }
    public void InstallShadowDefaults(string isolatedPath) { string generated=ProjectSettings.GlobalizePath("user://generated").Replace('\\','/'); string normalized=isolatedPath.Replace('\\','/'); if (!isolatedPath.StartsWith("res://.godot/",System.StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/",System.StringComparison.Ordinal) && !normalized.StartsWith(generated+"/",System.StringComparison.Ordinal)) return; _shadowDefaultsPathOverride=isolatedPath; if (_shadowDefaults is null) return; _shadowDefaults=new GameplayShadowDefaults(isolatedPath); _shadowDefaults.Initialize(_shadowGraphics); RefreshShadowControls(); }
    private void BuildPersistentShadowControls(VBoxContainer tab)
    {
        tab.AddChild(new Label { Text = "Directional shadow quality. Changes are saved and reapply after graphics presets.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer quality = TuningMenuUi.CreateSection(tab, "Quality");
        _shadowEnabled = new CheckButton { Name="SunShadows", Text="Sun shadows", ButtonPressed=_shadowGraphics?.RequestedSunShadowsEnabled ?? true }; _shadowEnabled.Toggled += v => SetShadowValue("Enabled", v); quality.AddChild(_shadowEnabled);
        _localShadowHandoffEnabled = new CheckButton { Name="LocalShadowHandoff", Text="Suspend sun shadows below local horizon", ButtonPressed=_shadowGraphics?.LocalShadowHandoffEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Keeps planet shadows available while the sun is safely below the local ring horizon." }; _localShadowHandoffEnabled.Toggled += value => SetShadowValue("LocalShadowHandoffEnabled", value); quality.AddChild(_localShadowHandoffEnabled);
        AddShadowSlider(quality, "SunShadowOffElevationDegrees", "Suspend below elevation (degrees)", -45, -1, .1, _shadowGraphics?.EffectiveSunShadowOffElevationDegrees ?? -18);
        AddShadowSlider(quality, "SunShadowOnElevationDegrees", "Resume above elevation (degrees)", -45, -1, .1, _shadowGraphics?.EffectiveSunShadowOnElevationDegrees ?? -16);
        AddShadowDepthOption(quality, _shadowGraphics?.EffectiveDirectionalShadowDepthBits ?? 16); AddShadowAtlasOption(quality, _shadowGraphics?.EffectiveDirectionalShadowAtlasSize ?? 8192); AddShadowOption(quality, "Filter", "Soft-shadow filter", new[] { "Hard", "Soft very low", "Soft low", "Soft medium", "Soft high", "Soft ultra" }, new[] { 0,1,2,3,4,5 }, _shadowGraphics?.EffectiveDirectionalShadowFilter ?? 3);
        VBoxContainer terrainMacro = TuningMenuUi.CreateSection(tab, "Terrain macro shadows");
        _terrainMacroShadowEnabled = new CheckButton { Name="TerrainMacroShadows", Text="Enable terrain macro shadows", ButtonPressed=_shadowGraphics?.EffectiveTerrainMacroShadowsEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Uses the terrain height summary to cast distant mountain shadows." };
        _terrainMacroShadowEnabled.Toggled += value => SetShadowValue("TerrainMacroEnabled", value);
        terrainMacro.AddChild(_terrainMacroShadowEnabled);
        _terrainMacroWallShadowsEnabled = new CheckButton { Name="TerrainMacroWallShadows", Text="Enable structural wall casting", ButtonPressed=_shadowGraphics?.EffectiveTerrainMacroWallShadowsEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Includes the ring's structural walls as terrain macro shadow casters." };
        _terrainMacroWallShadowsEnabled.Toggled += value => SetShadowValue("TerrainMacroWallShadowsEnabled", value);
        terrainMacro.AddChild(_terrainMacroWallShadowsEnabled);
        AddShadowSlider(terrainMacro, "TerrainMacroStrength", "Terrain shadow strength", 0, .8, .01, _shadowGraphics?.EffectiveTerrainMacroShadowStrength ?? .34);
        AddShadowSpinBox(terrainMacro, "TerrainMacroTraceSteps", "Terrain trace steps", 1, 32, 1, _shadowGraphics?.EffectiveTerrainMacroShadowTraceSteps ?? 10);
        AddShadowSpinBox(terrainMacro, "TerrainMacroMaxTraceMeters", "Maximum trace distance (m)", 4000, 320000, 1000, _shadowGraphics?.EffectiveTerrainMacroShadowMaxTraceMeters ?? 96000);
        AddShadowSpinBox(terrainMacro, "TerrainMacroFadeStartMeters", "Macro handoff near (m)", 0, 50000, 100, _shadowGraphics?.EffectiveTerrainMacroShadowFadeStartMeters ?? 3200);
        AddShadowSpinBox(terrainMacro, "TerrainMacroFadeEndMeters", "Macro handoff far (m)", 100, 60000, 100, _shadowGraphics?.EffectiveTerrainMacroShadowFadeEndMeters ?? 6500);
        VBoxContainer cascades = TuningMenuUi.CreateSection(tab, "Cascade coverage");
        AddShadowSpinBox(cascades, "NearCascadeMeters", "Near cascade distance (m)", RingGraphicsQualityController.MinimumNearCascadeMeters, RingGraphicsQualityController.MaximumNearCascadeMeters, 1, _shadowGraphics?.EffectiveNearCascadeMeters ?? RingGraphicsQualityController.DefaultNearCascadeMeters,
            "Focuses near shadow coverage, up to 40% of the shadow range. The requested distance is kept across presets.");
        AddShadowSlider(cascades, "Split2", "Cascade split 2", .02,.70,.001, _sun?.DirectionalShadowSplit2 ?? .06); AddShadowSlider(cascades, "Split3", "Cascade split 3", .03,.95,.001, _sun?.DirectionalShadowSplit3 ?? .28); AddShadowSlider(cascades, "FadeStart", "Shadow fade start", .50,.98,.01, _sun?.DirectionalShadowFadeStart ?? .88);
        VBoxContainer bias = TuningMenuUi.CreateSection(tab, "Bias / softness");
        AddShadowSlider(bias, "Bias", "Shadow bias", 0,1,.01, _sun?.ShadowBias ?? .08); AddShadowSlider(bias, "NormalBias", "Normal bias", 0,4,.05, _sun?.ShadowNormalBias ?? 1.5); AddShadowSlider(bias, "SunAngularDistance", "Sun softness (degrees)", 0, 3, .01, _sun?.LightAngularDistance ?? 0,
            "Zero disables contact-hardening shadows; the selected shadow filter still applies.");
        _sunStabilizationEnabled = new CheckButton
        {
            Name = "SunStabilizationEnabled",
            Text = "Stabilize moving sun",
            ButtonPressed = _dayNightCycle?.SunShadowStabilizationEnabled ?? true,
            Disabled = _dayNightCycle == null,
            TooltipText = "Holds the directional light between small sun-angle changes to reduce moving-shadow shimmer."
        };
        _sunStabilizationEnabled.Toggled += value => SetShadowValue("SunStabilizationEnabled", value);
        VBoxContainer stabilization = TuningMenuUi.CreateSection(tab, "Stabilization");
        stabilization.AddChild(_sunStabilizationEnabled);
        HSlider sunStep = AddShadowSlider(stabilization, "SunStepDegrees", "Sun update step (degrees)", 0, .05, .001, _dayNightCycle?.SunShadowStepDegrees ?? .01,
            "The directional light updates after this angular change. The clock, sky and eclipses keep moving.");
        sunStep.Editable = _dayNightCycle != null;
        stabilization.AddChild(new Label
        {
            Text = "Larger steps can make shadows jump; smaller steps are used near the horizon.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        });
        _pauseDayNight = new CheckButton
        {
            Name = "PauseDayNightTest",
            Text = "Pause day/night (test)",
            ButtonPressed = _dayNightCycle?.Paused ?? false,
            Disabled = _dayNightCycle == null,
            TooltipText = "Temporary test control for isolating sun motion. This setting is not saved."
        };
        _pauseDayNight.Toggled += value => { if (_dayNightCycle != null) _dayNightCycle.Paused = value; };
        stabilization.AddChild(_pauseDayNight);
        var row = new HBoxContainer(); var save = new Button { Name="ShadowSaveNow", Text="Save shadows", SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; save.Pressed += () => FlushShadowDefaults(); row.AddChild(save); var reset = new Button { Name="ShadowResetDefaults", Text="Reset shadow overrides", SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; reset.Pressed += () => { _shadowDefaults.Reset(_shadowGraphics); _shadowSaveDelay=0; FlushShadowDefaults(); RefreshShadowControls(); }; row.AddChild(reset); tab.AddChild(row);
        _shadowStatus = new Label { Name="ShadowRuntimeStatus", AutowrapMode=TextServer.AutowrapMode.WordSmart, Modulate=RingUiStyle.Muted }; tab.AddChild(_shadowStatus); UpdateShadowStatus();
    }
    private HSlider AddShadowSlider(VBoxContainer parent, string key, string label, double min, double max, double step, double initial, string tooltip = "")
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        var slider = new HSlider
        {
            Name = "Shadow" + key,
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initial,
            TooltipText = tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var value = new Label { Name = "Shadow" + key + "Value", CustomMinimumSize = new Vector2(55, 0), Text = initial.ToString("0.###") };
        slider.ValueChanged += changed => { value.Text = changed.ToString("0.###"); SetShadowValue(key, (float)changed); };
        _shadowRanges[key] = slider;
        _shadowValueLabels[key] = value;
        row.AddChild(slider);
        row.AddChild(value);
        parent.AddChild(row);
        return slider;
    }
    private SpinBox AddShadowSpinBox(VBoxContainer parent, string key, string label, double min, double max, double step, double initial, string tooltip = "")
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        var spin = new SpinBox { Name = "Shadow" + key, MinValue = min, MaxValue = max, Step = step, Value = initial, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        spin.ValueChanged += changed => SetShadowValue(key, (float)changed);
        _shadowRanges[key] = spin;
        row.AddChild(spin);
        parent.AddChild(row);
        return spin;
    }
    private void AddShadowOption(VBoxContainer parent,string key,string label,string[] labels,int[] values,int initial) { var row=new HBoxContainer(); row.AddChild(new Label { Text=label, CustomMinimumSize=new Vector2(150,0) }); var option=new OptionButton { Name="Shadow"+key, SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; for(int i=0;i<labels.Length;i++) { option.AddItem(labels[i],values[i]); if(values[i]==initial) option.Select(i); } option.ItemSelected += i => SetShadowValue(key,option.GetItemId((int)i)); _shadowOptions[key]=option; row.AddChild(option); parent.AddChild(row); }
    private void AddShadowDepthOption(VBoxContainer parent, int initial)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = "Shadow depth precision", CustomMinimumSize = new Vector2(150, 0) });
        var option = new OptionButton { Name = "ShadowDepthBits", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Use 32-bit if surfaces develop speckled self-shadowing." };
        option.AddItem("16-bit (lower memory)", 16); option.AddItem("32-bit (higher precision)", 32);
        SetOptionSelection(option, initial);
        option.ItemSelected += index => { SetShadowValue("DepthBits", option.GetItemId((int)index)); RefreshShadowControls(); };
        _shadowOptions["DepthBits"] = option; row.AddChild(option); parent.AddChild(row);
        parent.AddChild(new Label { Text = "Use 32-bit if surfaces develop speckled self-shadowing.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
    }
    private void AddShadowAtlasOption(VBoxContainer parent, int initial)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = "Atlas resolution", CustomMinimumSize = new Vector2(150, 0) });
        var option = new OptionButton { Name = "ShadowAtlasSize", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        int limit = _shadowGraphics?.DirectionalShadowAtlasHardwareLimit ?? RingGraphicsQualityController.UnverifiedDirectionalShadowAtlasLimit;
        bool unverified = _shadowGraphics?.DirectionalShadowAtlasLimitIsUnverified ?? true;
        for (int index = 0; index < RingGraphicsQualityController.DirectionalShadowAtlasSizes.Count; index++)
        {
            int size = RingGraphicsQualityController.DirectionalShadowAtlasSizes[index];
            option.AddItem(size.ToString(), size);
            bool supported = size <= limit;
            option.GetPopup().SetItemDisabled(index, !supported);
            if (size == initial) option.Select(index);
        }
        option.ItemSelected += index =>
        {
            int size = option.GetItemId((int)index);
            if (_shadowGraphics?.IsDirectionalShadowAtlasSizeSupported(size) != true) { RefreshShadowControls(); return; }
            SetShadowValue("AtlasSize", size);
        };
        _shadowOptions["AtlasSize"] = option; row.AddChild(option); parent.AddChild(row);
        UpdateShadowAtlasOptionPresentation(option, _shadowGraphics?.EffectiveDirectionalShadowDepthBits ?? 16, limit, unverified);
        parent.AddChild(new Label { Text = "Estimates cover raw shadow depth storage; they are not measured VRAM use.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
    }
    private static string FormatShadowDepthMemory(ulong bytes)
    {
        const double gib = 1024d * 1024d * 1024d;
        return bytes >= 512UL * 1024UL * 1024UL ? $"{bytes / gib:0.#} GiB" : $"{bytes / (1024d * 1024d):0.#} MiB";
    }
    private static void SetOptionSelection(OptionButton option, int value) { for (int i = 0; i < option.ItemCount; i++) if (option.GetItemId(i) == value) { option.Select(i); return; } }
    private static void UpdateShadowAtlasOptionPresentation(OptionButton option, int depthBits, int limit, bool unverified)
    {
        for (int index = 0; index < option.ItemCount; index++)
        {
            int size = option.GetItemId(index); bool supported = size <= limit;
            ulong bytes = RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(size, depthBits);
            string memory = FormatShadowDepthMemory(bytes);
            string text = $"{size} ({memory} raw depth)";
            string tooltip = supported
                ? $"Estimated raw depth storage: {memory} at {depthBits}-bit; other graphics features use additional memory."
                : unverified ? $"Unavailable: no verified RenderingDevice limit; conservative fallback is {limit}px. Estimated raw depth storage: {memory} at {depthBits}-bit."
                : $"Unavailable: GPU texture/framebuffer limit is {limit}px. Estimated raw depth storage: {memory} at {depthBits}-bit.";
            if (option.GetItemText(index) != text) option.SetItemText(index, text);
            if (option.GetPopup().GetItemTooltip(index) != tooltip) option.GetPopup().SetItemTooltip(index, tooltip);
        }
    }
    private void SetShadowValue(string key, Variant value) { if (_shadowDefaults.SetOverride(_shadowGraphics,key,value)) { _shadowSaveDelay=.5; _shadowRetryDelay=0; UpdateShadowStatus(); } }
    private void RefreshShadowControls()
    {
        if (_shadowGraphics == null) return;
        if (_sun != null)
        {
            SetRange("Split2", _sun.DirectionalShadowSplit2);
            SetRange("Split3", _sun.DirectionalShadowSplit3);
            SetRange("FadeStart", _sun.DirectionalShadowFadeStart);
            SetRange("Bias", _sun.ShadowBias);
            SetRange("NormalBias", _sun.ShadowNormalBias);
            SetRange("SunAngularDistance", _sun.LightAngularDistance);
            _shadowEnabled?.SetPressedNoSignal(_shadowGraphics.RequestedSunShadowsEnabled);
            _localShadowHandoffEnabled?.SetPressedNoSignal(_shadowGraphics.LocalShadowHandoffEnabled);
        }
        if (_dayNightCycle != null)
        {
            SetRange("SunStepDegrees", _dayNightCycle.SunShadowStepDegrees);
            _sunStabilizationEnabled?.SetPressedNoSignal(_dayNightCycle.SunShadowStabilizationEnabled);
            _pauseDayNight?.SetPressedNoSignal(_dayNightCycle.Paused);
        }
        SetOption("AtlasSize", _shadowGraphics.EffectiveDirectionalShadowAtlasSize);
        SetOption("DepthBits", _shadowGraphics.EffectiveDirectionalShadowDepthBits);
        SetOption("Filter", _shadowGraphics.EffectiveDirectionalShadowFilter);
        SetRange("NearCascadeMeters", _shadowGraphics.EffectiveNearCascadeMeters);
        SetRange("TerrainMacroStrength", _shadowGraphics.EffectiveTerrainMacroShadowStrength);
        SetRange("TerrainMacroTraceSteps", _shadowGraphics.EffectiveTerrainMacroShadowTraceSteps);
        SetRange("TerrainMacroMaxTraceMeters", _shadowGraphics.EffectiveTerrainMacroShadowMaxTraceMeters);
        SetRange("TerrainMacroFadeStartMeters", _shadowGraphics.EffectiveTerrainMacroShadowFadeStartMeters);
        SetRange("TerrainMacroFadeEndMeters", _shadowGraphics.EffectiveTerrainMacroShadowFadeEndMeters);
        SetRange("SunShadowOffElevationDegrees", _shadowGraphics.EffectiveSunShadowOffElevationDegrees);
        SetRange("SunShadowOnElevationDegrees", _shadowGraphics.EffectiveSunShadowOnElevationDegrees);
        _terrainMacroShadowEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveTerrainMacroShadowsEnabled);
        _terrainMacroWallShadowsEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveTerrainMacroWallShadowsEnabled);
        if (_shadowOptions.TryGetValue("AtlasSize", out OptionButton? atlas))
            UpdateShadowAtlasOptionPresentation(atlas, _shadowGraphics.EffectiveDirectionalShadowDepthBits, _shadowGraphics.DirectionalShadowAtlasHardwareLimit, _shadowGraphics.DirectionalShadowAtlasLimitIsUnverified);
        UpdateShadowStatus();
    }
    private void SetRange(string key,float value) { if (_shadowRanges.TryGetValue(key,out Range? range)) range.SetValueNoSignal(value); if (_shadowValueLabels.TryGetValue(key,out Label? label)) label.Text=value.ToString("0.###"); }
    private void SetOption(string key,int value) { if (_shadowOptions.TryGetValue(key,out OptionButton? option)) SetOptionSelection(option, value); }
    private void UpdateShadowStatus()
    {
        if (_shadowStatus == null || _shadowGraphics == null) return;
        string state = _shadowDefaults.LastError.Length > 0
            ? "Shadow settings could not be saved; retrying."
            : _shadowDefaults.Dirty ? "Shadow settings pending." : "Shadow settings saved.";
        string stabilization = _dayNightCycle == null
            ? " Sun stabilization unavailable."
            : $" Sun step: {_dayNightCycle.EffectiveSunShadowStepDegrees:0.###}°, lag: {_dayNightCycle.SunShadowAngularLagDegrees:0.###}°.";
        string atlasLimit = _shadowGraphics.DirectionalShadowAtlasLimitIsUnverified
            ? $" Atlas limit: {_shadowGraphics.DirectionalShadowAtlasHardwareLimit}px conservative fallback (unverified)."
            : $" GPU atlas limit: {_shadowGraphics.DirectionalShadowAtlasHardwareLimit}px.";
        string requested = _shadowGraphics.RequestedDirectionalShadowAtlasSize == _shadowGraphics.EffectiveDirectionalShadowAtlasSize
            ? ""
            : $" Requested {_shadowGraphics.RequestedDirectionalShadowAtlasSize}px.";
        ulong depthBytes = _shadowGraphics.EffectiveDirectionalShadowAtlasMinimumDepthBytes;
        _shadowStatus.Text = $"{state} Effective: {_shadowGraphics.EffectiveDirectionalShadowAtlasSize}px atlas, "
            + $"{_shadowGraphics.EffectiveDirectionalShadowDepthBits}-bit depth ({FormatShadowDepthMemory(depthBytes)} estimated raw depth), filter {_shadowGraphics.EffectiveDirectionalShadowFilter}, "
            + $"near cascade {_shadowGraphics.EffectiveNearCascadeMeters:0} m, total range {_shadowGraphics.EffectiveShadowDistanceMeters:0} m."
            + $" Sun handoff: {_shadowGraphics.LocalShadowHandoffReason}."
            + requested + atlasLimit + $"{stabilization} Presets coordinate this range with terrain and cloud-shadow handoffs.";
    }
}
